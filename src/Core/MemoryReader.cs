using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using TalosForge.Core.Abstractions;

namespace TalosForge.Core;

/// <summary>
/// External WoW memory reader using Kernel32 APIs.
/// </summary>
public sealed class MemoryReader : IMemoryReader
{
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    private static readonly Lazy<MemoryReader> LazyInstance =
        new(() => new MemoryReader("Wow"));

    private readonly object _syncRoot = new();
    private readonly string _processName;

    private Process? _wowProcess;
    private IntPtr _processHandle;
    private bool _disposed;

    private MemoryReader(string processName)
    {
        _processName = processName;
    }

    /// <summary>
    /// Singleton instance for shared memory access.
    /// </summary>
    public static MemoryReader Instance => LazyInstance.Value;

    /// <summary>
    /// Gets whether the reader is currently attached to WoW.
    /// </summary>
    public bool IsAttached { get; private set; }

    /// <summary>
    /// Gets the attached process main module base address.
    /// </summary>
    public IntPtr BaseAddress { get; private set; }

    /// <summary>
    /// Gets the attached WoW process.
    /// </summary>
    public Process WowProcess =>
        _wowProcess ?? throw new InvalidOperationException("WoW process is not attached.");

    /// <summary>
    /// Attaches to the first running WoW process.
    /// </summary>
    /// <returns>True when attached, false when process not found.</returns>
    public bool Attach()
    {
        lock (_syncRoot)
        {
            ThrowIfDisposed();

            if (IsAttached && _wowProcess is { HasExited: false })
            {
                return true;
            }

            DetachInternal();

            var process = Process.GetProcessesByName(_processName).FirstOrDefault();
            if (process == null)
            {
                return false;
            }

            var desiredAccess = ProcessVmRead |
                                ProcessVmWrite |
                                ProcessVmOperation |
                                ProcessQueryInformation |
                                ProcessQueryLimitedInformation;

            var handle = OpenProcess(desiredAccess, false, process.Id);
            if (handle == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                throw new Win32Exception(error, $"OpenProcess failed for PID {process.Id}.");
            }

            if (!IsTarget32Bit(handle))
            {
                CloseHandle(handle);
                process.Dispose();
                throw new InvalidOperationException("Attached process is not 32-bit. WoW 3.3.5a requires 32-bit semantics.");
            }

            IntPtr baseAddress;
            try
            {
                baseAddress = process.MainModule?.BaseAddress ?? IntPtr.Zero;
            }
            catch (Exception ex)
            {
                CloseHandle(handle);
                process.Dispose();
                throw new InvalidOperationException($"Unable to read main module for PID {process.Id}.", ex);
            }

            if (baseAddress == IntPtr.Zero)
            {
                CloseHandle(handle);
                process.Dispose();
                throw new InvalidOperationException("Main module base address is zero.");
            }

            _wowProcess = process;
            _processHandle = handle;
            BaseAddress = baseAddress;
            IsAttached = true;

            return true;
        }
    }

    /// <summary>
    /// Detaches from the current process and closes native handles.
    /// </summary>
    public void Detach()
    {
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            DetachInternal();
        }
    }

    /// <summary>
    /// Reads a value type from process memory.
    /// </summary>
    public T Read<T>(IntPtr address) where T : struct
    {
        lock (_syncRoot)
        {
            EnsureAttached();
            var size = Marshal.SizeOf(typeof(T));
            var data = ReadBytesInternal(address, size);

            var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
            try
            {
                return (T)Marshal.PtrToStructure(handle.AddrOfPinnedObject(), typeof(T))!;
            }
            finally
            {
                handle.Free();
            }
        }
    }

    /// <summary>
    /// Reads a struct from process memory.
    /// </summary>
    public T ReadStruct<T>(IntPtr address) where T : struct
    {
        return Read<T>(address);
    }

    /// <summary>
    /// Reads an ASCII string from process memory until null terminator or max length.
    /// </summary>
    public string ReadString(IntPtr address, int maxLength = 256)
    {
        if (maxLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxLength), "Max length must be greater than zero.");
        }

        lock (_syncRoot)
        {
            EnsureAttached();
            var bytes = ReadBytesInternal(address, maxLength);
            var terminator = Array.IndexOf(bytes, (byte)0);
            var count = terminator >= 0 ? terminator : bytes.Length;
            return Encoding.ASCII.GetString(bytes, 0, count);
        }
    }

    /// <summary>
    /// Reads a 32-bit pointer from process memory.
    /// </summary>
    public IntPtr ReadPointer(IntPtr address)
    {
        var value = Read<uint>(address);
        return new IntPtr(unchecked((long)value));
    }

    /// <summary>
    /// Resolves a pointer chain relative to <see cref="BaseAddress"/>.
    /// </summary>
    /// <param name="offsets">
    /// Offsets where the first value is added to base address.
    /// Example: ResolveChain(0x00C7B5A8, 0x6B04, 0xE8)
    /// </param>
    public IntPtr ResolveChain(params int[] offsets)
    {
        lock (_syncRoot)
        {
            EnsureAttached();
            return PointerChainResolver.Resolve(BaseAddress, ReadPointer, offsets);
        }
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }

            DetachInternal();
            _disposed = true;
        }

        GC.SuppressFinalize(this);
    }

    private static bool IsTarget32Bit(IntPtr processHandle)
    {
        if (!Environment.Is64BitOperatingSystem)
        {
            return true;
        }

        if (!IsWow64Process(processHandle, out var isWow64))
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, "IsWow64Process failed.");
        }

        return isWow64;
    }

    private byte[] ReadBytesInternal(IntPtr address, int size)
    {
        var buffer = new byte[size];
        var ok = ReadProcessMemory(_processHandle, address, buffer, size, out var bytesRead);
        if (!ok || bytesRead.ToInt64() != size)
        {
            var error = Marshal.GetLastWin32Error();
            throw new Win32Exception(error, $"ReadProcessMemory failed at 0x{address.ToInt64():X}.");
        }

        return buffer;
    }

    private void EnsureAttached()
    {
        ThrowIfDisposed();

        if (!IsAttached || _wowProcess == null || _processHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("MemoryReader is not attached.");
        }

        if (_wowProcess.HasExited)
        {
            DetachInternal();
            throw new InvalidOperationException("WoW process exited.");
        }
    }

    private void DetachInternal()
    {
        if (_processHandle != IntPtr.Zero)
        {
            CloseHandle(_processHandle);
            _processHandle = IntPtr.Zero;
        }

        _wowProcess?.Dispose();
        _wowProcess = null;

        BaseAddress = IntPtr.Zero;
        IsAttached = false;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(MemoryReader));
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint processAccess, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(
        IntPtr processHandle,
        IntPtr baseAddress,
        [Out] byte[] buffer,
        int size,
        out IntPtr numberOfBytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool IsWow64Process(IntPtr processHandle, out bool wow64Process);
}
