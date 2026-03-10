using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace TalosForge.Core
{
    /// <summary>
    /// External WoW memory reader using Kernel32 APIs.
    /// </summary>
    public sealed class MemoryReader : IDisposable
    {
        private const string DefaultProcessName = "Wow";
        private const uint ProcessVmRead = 0x0010;
        private const uint ProcessVmWrite = 0x0020;
        private const uint ProcessVmOperation = 0x0008;
        private const uint ProcessQueryInformation = 0x0400;
        private const uint ProcessQueryLimitedInformation = 0x1000;

        private static readonly Lazy<MemoryReader> LazyInstance =
            new Lazy<MemoryReader>(() => new MemoryReader());

        private readonly object _syncRoot = new object();

        // Thread-safe cache stub for later cache system.
        private readonly ConcurrentDictionary<string, byte[]> _cacheStub =
            new ConcurrentDictionary<string, byte[]>();

        private Process _wowProcess;
        private IntPtr _processHandle;
        private bool _disposed;

        private MemoryReader()
        {
        }

        /// <summary>
        /// Singleton instance for shared memory access.
        /// </summary>
        public static MemoryReader Instance
        {
            get { return LazyInstance.Value; }
        }

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
        public Process WowProcess
        {
            get
            {
                if (_wowProcess == null)
                {
                    throw new InvalidOperationException("WoW process is not attached.");
                }

                return _wowProcess;
            }
        }

        /// <summary>
        /// Attaches to the first running "Wow" process.
        /// </summary>
        /// <returns><c>true</c> when attached; otherwise <c>false</c> when process not found.</returns>
        public bool Attach()
        {
            lock (_syncRoot)
            {
                ThrowIfDisposed();

                if (IsAttached && _wowProcess != null && !_wowProcess.HasExited)
                {
                    return true;
                }

                DetachInternal();

                var process = Process.GetProcessesByName(DefaultProcessName).FirstOrDefault();
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
                    throw new Win32Exception(error, string.Format("OpenProcess failed for PID {0}.", process.Id));
                }

                IntPtr baseAddress;
                try
                {
                    baseAddress = process.MainModule == null ? IntPtr.Zero : process.MainModule.BaseAddress;
                }
                catch (Exception ex)
                {
                    CloseHandle(handle);
                    process.Dispose();
                    throw new InvalidOperationException(
                        string.Format("Unable to read main module for PID {0}.", process.Id), ex);
                }

                if (baseAddress == IntPtr.Zero)
                {
                    CloseHandle(handle);
                    process.Dispose();
                    throw new InvalidOperationException("Main module base address is zero.");
                }

                // Requested API inclusion. For external readers, process.MainModule.BaseAddress is authoritative.
                var moduleHandle = GetModuleHandle("Wow.exe");
                if (moduleHandle == IntPtr.Zero)
                {
                    // This can be zero for external processes; keep attach behavior based on process.MainModule.
                }

                _wowProcess = process;
                _processHandle = handle;
                BaseAddress = baseAddress;
                IsAttached = true;

                Console.WriteLine(string.Format("Attached to WoW (PID {0})", process.Id));
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
                    return (T)Marshal.PtrToStructure(handle.AddrOfPinnedObject(), typeof(T));
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
                throw new ArgumentOutOfRangeException("maxLength", "Max length must be greater than zero.");
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
            if (offsets == null || offsets.Length == 0)
            {
                throw new ArgumentException("At least one offset is required.", "offsets");
            }

            lock (_syncRoot)
            {
                EnsureAttached();

                var current = IntPtr.Add(BaseAddress, offsets[0]);
                for (var i = 1; i < offsets.Length; i++)
                {
                    current = ReadPointer(current);
                    if (current == IntPtr.Zero)
                    {
                        return IntPtr.Zero;
                    }

                    current = IntPtr.Add(current, offsets[i]);
                }

                return current;
            }
        }

        /// <summary>
        /// Clears the in-memory cache stub.
        /// </summary>
        public void ClearCache()
        {
            _cacheStub.Clear();
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

        private byte[] ReadBytesInternal(IntPtr address, int size)
        {
            var buffer = new byte[size];
            IntPtr bytesRead;
            var ok = ReadProcessMemory(_processHandle, address, buffer, size, out bytesRead);
            if (!ok || bytesRead.ToInt64() != size)
            {
                var error = Marshal.GetLastWin32Error();
                throw new Win32Exception(
                    error,
                    string.Format("ReadProcessMemory failed at 0x{0:X}.", address.ToInt64()));
            }

            _cacheStub[string.Format("{0:X}:{1}", address.ToInt64(), size)] = (byte[])buffer.Clone();
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

            if (_wowProcess != null)
            {
                _wowProcess.Dispose();
                _wowProcess = null;
            }

            BaseAddress = IntPtr.Zero;
            IsAttached = false;
            _cacheStub.Clear();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException("MemoryReader");
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

        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        private static extern IntPtr GetModuleHandle(string moduleName);
    }
}
