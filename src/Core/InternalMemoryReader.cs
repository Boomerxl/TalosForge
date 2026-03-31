using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using TalosForge.Core.Abstractions;

namespace TalosForge.Core;

/// <summary>
/// Memory reader that delegates all reads to the in-process native agent via named pipe.
/// Eliminates external OpenProcess/ReadProcessMemory calls entirely.
/// </summary>
public sealed class InternalMemoryReader : IMemoryReader
{
    private readonly string _processName;
    private readonly object _syncRoot = new();
    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Process? _wowProcess;
    private bool _disposed;

    public InternalMemoryReader(string processName = "Wow")
    {
        _processName = processName;
    }

    public bool IsAttached { get; private set; }
    public IntPtr BaseAddress { get; private set; }
    public Process WowProcess => _wowProcess ?? throw new InvalidOperationException("Not attached.");

    public bool Attach()
    {
        lock (_syncRoot)
        {
            ThrowIfDisposed();

            if (IsAttached && _pipe is { IsConnected: true } && _wowProcess is { HasExited: false })
                return true;

            DetachInternal();

            var process = Process.GetProcessesByName(_processName).FirstOrDefault();
            if (process == null)
                return false;

            var pipeName = DiscoverPipeName(process.Id);
            if (string.IsNullOrEmpty(pipeName))
            {
                process.Dispose();
                return false;
            }

            try
            {
                var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.None);
                pipe.Connect(3000);

                _pipe = pipe;
                _writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };
                _reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                _wowProcess = process;

                IntPtr baseAddr;
                try { baseAddr = process.MainModule?.BaseAddress ?? IntPtr.Zero; }
                catch { baseAddr = IntPtr.Zero; }

                BaseAddress = baseAddr;
                IsAttached = true;
                return true;
            }
            catch
            {
                process.Dispose();
                return false;
            }
        }
    }

    public void Detach()
    {
        lock (_syncRoot)
        {
            ThrowIfDisposed();
            DetachInternal();
        }
    }

    public T Read<T>(IntPtr address) where T : struct
    {
        var size = Marshal.SizeOf(typeof(T));
        var bytes = ReadBytesFromAgent(address, size);

        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            return (T)Marshal.PtrToStructure(handle.AddrOfPinnedObject(), typeof(T))!;
        }
        finally
        {
            handle.Free();
        }
    }

    public T ReadStruct<T>(IntPtr address) where T : struct => Read<T>(address);

    public string ReadString(IntPtr address, int maxLength = 256)
    {
        if (maxLength <= 0) throw new ArgumentOutOfRangeException(nameof(maxLength));
        var bytes = ReadBytesFromAgent(address, maxLength);
        var terminator = Array.IndexOf(bytes, (byte)0);
        var count = terminator >= 0 ? terminator : bytes.Length;
        return Encoding.ASCII.GetString(bytes, 0, count);
    }

    public IntPtr ReadPointer(IntPtr address)
    {
        var value = Read<uint>(address);
        return new IntPtr(unchecked((long)value));
    }

    public IntPtr ResolveChain(params int[] offsets)
    {
        lock (_syncRoot)
        {
            EnsureAttached();
            return PointerChainResolver.Resolve(BaseAddress, ReadPointer, offsets);
        }
    }

    /// <summary>
    /// Issues a WalkObjects command to the native agent and returns the raw JSON.
    /// This is a single round-trip that replaces dozens of ReadProcessMemory calls.
    /// </summary>
    public string? WalkObjectsJson()
    {
        lock (_syncRoot)
        {
            EnsureAttached();
            var result = SendCommand("WalkObjects", "{}");
            return result.Success ? result.Message : null;
        }
    }

    public void Dispose()
    {
        lock (_syncRoot)
        {
            if (_disposed) return;
            DetachInternal();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    private byte[] ReadBytesFromAgent(IntPtr address, int size)
    {
        lock (_syncRoot)
        {
            EnsureAttached();
            var payload = $"{{\"address\":\"0x{address.ToInt64():X}\",\"size\":{size}}}";
            var result = SendCommand("ReadBytes", payload);
            if (!result.Success)
                throw new InvalidOperationException($"ReadBytes failed at 0x{address.ToInt64():X}: {result.Message}");

            return HexToBytes(result.Message);
        }
    }

    private (bool Success, string Message) SendCommand(string opcode, string payload)
    {
        if (_writer == null || _reader == null || _pipe == null || !_pipe.IsConnected)
            throw new InvalidOperationException("Pipe not connected.");

        _writer.WriteLine(opcode);
        _writer.WriteLine(payload);
        _writer.WriteLine("5000");
        _writer.Flush();

        var successLine = _reader.ReadLine();
        var code = _reader.ReadLine();
        var message = _reader.ReadLine();
        var _ = _reader.ReadLine(); // echo payload

        if (successLine == null || code == null || message == null)
            throw new InvalidOperationException("Pipe response truncated.");

        var success = successLine.Trim() == "1";
        return (success, message);
    }

    private static byte[] HexToBytes(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return Array.Empty<byte>();
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
            bytes[i] = byte.Parse(hex.AsSpan(i * 2, 2), NumberStyles.HexNumber);
        return bytes;
    }

    private static string? DiscoverPipeName(int processId)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), $"TalosForge.pipe.{processId}");
            if (!File.Exists(path)) return null;
            var full = File.ReadAllText(path).Trim();
            if (string.IsNullOrEmpty(full)) return null;
            const string prefix = @"\\.\pipe\";
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full[prefix.Length..] : full;
        }
        catch { return null; }
    }

    private void EnsureAttached()
    {
        ThrowIfDisposed();
        if (!IsAttached || _pipe == null || !_pipe.IsConnected)
            throw new InvalidOperationException("InternalMemoryReader not attached.");
        if (_wowProcess is { HasExited: true })
        {
            DetachInternal();
            throw new InvalidOperationException("WoW process exited.");
        }
    }

    private void DetachInternal()
    {
        try { _writer?.Dispose(); } catch { }
        try { _reader?.Dispose(); } catch { }
        try { _pipe?.Dispose(); } catch { }
        _writer = null;
        _reader = null;
        _pipe = null;
        _wowProcess?.Dispose();
        _wowProcess = null;
        BaseAddress = IntPtr.Zero;
        IsAttached = false;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(InternalMemoryReader));
    }
}
