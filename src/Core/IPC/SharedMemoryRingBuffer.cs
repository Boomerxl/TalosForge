using System.IO.MemoryMappedFiles;
using System.Text;

namespace TalosForge.Core.IPC;

/// <summary>
/// Lock-protected shared-memory ring buffer with a fixed header.
/// </summary>
public sealed class SharedMemoryRingBuffer : IDisposable
{
    private const int HeaderSize = 20;
    private const int Magic = 0x54464F52; // TFOR
    private const int Version = 1;

    private readonly string _name;
    private readonly int _capacity;
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _accessor;
    private readonly Mutex _mutex;

    private bool _disposed;

    public SharedMemoryRingBuffer(string name, int capacity)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Ring name is required.", nameof(name));
        }

        if (capacity < 256)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be at least 256 bytes.");
        }

        _name = name;
        _capacity = capacity;
        _mmf = MemoryMappedFile.CreateOrOpen(name, HeaderSize + capacity, MemoryMappedFileAccess.ReadWrite);
        _accessor = _mmf.CreateViewAccessor(0, HeaderSize + capacity, MemoryMappedFileAccess.ReadWrite);
        _mutex = new Mutex(false, $"Global\\{name}.mutex");

        InitializeHeader();
    }

    public string Name => _name;
    public int Capacity => _capacity;

    public bool TryWrite(byte[] payload)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(payload);

        var frame = BuildFrame(payload);

        return WithLock(() =>
        {
            var writeIndex = ReadHeaderInt(12);
            var readIndex = ReadHeaderInt(16);

            ValidateIndices(writeIndex, readIndex);

            var free = FreeSpace(writeIndex, readIndex);
            if (frame.Length > free)
            {
                return false;
            }

            WriteBytes(writeIndex, frame);
            writeIndex = (writeIndex + frame.Length) % _capacity;
            WriteHeaderInt(12, writeIndex);
            return true;
        });
    }

    public bool TryRead(out byte[] payload)
    {
        ThrowIfDisposed();
        payload = Array.Empty<byte>();

        return WithLock(() =>
        {
            var writeIndex = ReadHeaderInt(12);
            var readIndex = ReadHeaderInt(16);

            ValidateIndices(writeIndex, readIndex);

            if (writeIndex == readIndex)
            {
                return false;
            }

            var frameLengthBytes = ReadBytes(readIndex, sizeof(int));
            var frameLength = BitConverter.ToInt32(frameLengthBytes, 0);
            if (frameLength <= 0 || frameLength > _capacity)
            {
                ResetIndices();
                return false;
            }

            var frameBytes = ReadBytes((readIndex + sizeof(int)) % _capacity, frameLength);
            readIndex = (readIndex + sizeof(int) + frameLength) % _capacity;
            WriteHeaderInt(16, readIndex);

            payload = frameBytes;
            return true;
        });
    }

    public string DebugHeader()
    {
        return WithLock(() =>
        {
            var magic = ReadHeaderInt(0);
            var version = ReadHeaderInt(4);
            var capacity = ReadHeaderInt(8);
            var writeIndex = ReadHeaderInt(12);
            var readIndex = ReadHeaderInt(16);
            return $"magic={magic:X} version={version} capacity={capacity} write={writeIndex} read={readIndex}";
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _accessor.Dispose();
        _mmf.Dispose();
        _mutex.Dispose();
        _disposed = true;
    }

    private void InitializeHeader()
    {
        WithLock(() =>
        {
            var currentMagic = ReadHeaderInt(0);
            var currentVersion = ReadHeaderInt(4);
            var currentCapacity = ReadHeaderInt(8);

            if (currentMagic != Magic || currentVersion != Version || currentCapacity != _capacity)
            {
                WriteHeaderInt(0, Magic);
                WriteHeaderInt(4, Version);
                WriteHeaderInt(8, _capacity);
                WriteHeaderInt(12, 0);
                WriteHeaderInt(16, 0);
            }
        });
    }

    private void ResetIndices()
    {
        WriteHeaderInt(12, 0);
        WriteHeaderInt(16, 0);
    }

    private void ValidateIndices(int writeIndex, int readIndex)
    {
        if (writeIndex < 0 || writeIndex >= _capacity || readIndex < 0 || readIndex >= _capacity)
        {
            ResetIndices();
            throw new InvalidOperationException("Ring header is corrupted (indices out of range).");
        }
    }

    private int FreeSpace(int writeIndex, int readIndex)
    {
        if (writeIndex >= readIndex)
        {
            return _capacity - (writeIndex - readIndex) - 1;
        }

        return readIndex - writeIndex - 1;
    }

    private static byte[] BuildFrame(byte[] payload)
    {
        var frame = new byte[sizeof(int) + payload.Length];
        Array.Copy(BitConverter.GetBytes(payload.Length), 0, frame, 0, sizeof(int));
        Array.Copy(payload, 0, frame, sizeof(int), payload.Length);
        return frame;
    }

    private byte[] ReadBytes(int start, int length)
    {
        var bytes = new byte[length];
        for (var i = 0; i < length; i++)
        {
            var index = (start + i) % _capacity;
            bytes[i] = _accessor.ReadByte(HeaderSize + index);
        }

        return bytes;
    }

    private void WriteBytes(int start, byte[] bytes)
    {
        for (var i = 0; i < bytes.Length; i++)
        {
            var index = (start + i) % _capacity;
            _accessor.Write(HeaderSize + index, bytes[i]);
        }
    }

    private int ReadHeaderInt(int offset)
    {
        return _accessor.ReadInt32(offset);
    }

    private void WriteHeaderInt(int offset, int value)
    {
        _accessor.Write(offset, value);
    }

    private T WithLock<T>(Func<T> action)
    {
        var lockTaken = _mutex.WaitOne(TimeSpan.FromMilliseconds(100));
        if (!lockTaken)
        {
            throw new TimeoutException($"Timed out waiting for ring lock {_name}.");
        }

        try
        {
            return action();
        }
        finally
        {
            _mutex.ReleaseMutex();
        }
    }

    private void WithLock(Action action)
    {
        WithLock(() =>
        {
            action();
            return true;
        });
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SharedMemoryRingBuffer));
        }
    }
}
