using System.Diagnostics;
using System.Runtime.InteropServices;
using TalosForge.Core.Abstractions;

namespace TalosForge.Tests.Core.Fakes;

internal sealed class FakeMemoryReader : IMemoryReader
{
    private readonly Dictionary<long, byte[]> _memory = new();

    public bool IsAttached { get; private set; } = true;
    public IntPtr BaseAddress { get; set; } = new(0x400000);
    public Process WowProcess => Process.GetCurrentProcess();

    public bool Attach()
    {
        IsAttached = true;
        return true;
    }

    public void Detach()
    {
        IsAttached = false;
    }

    public void Set<T>(IntPtr address, T value) where T : struct
    {
        var size = Marshal.SizeOf<T>();
        var bytes = new byte[size];
        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            Marshal.StructureToPtr(value, handle.AddrOfPinnedObject(), false);
            _memory[address.ToInt64()] = bytes;
        }
        finally
        {
            handle.Free();
        }
    }

    public T Read<T>(IntPtr address) where T : struct
    {
        if (!_memory.TryGetValue(address.ToInt64(), out var bytes))
        {
            throw new InvalidOperationException($"Address 0x{address.ToInt64():X} not mapped.");
        }

        var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            return Marshal.PtrToStructure<T>(handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
        }
    }

    public T ReadStruct<T>(IntPtr address) where T : struct
    {
        return Read<T>(address);
    }

    public string ReadString(IntPtr address, int maxLength = 256)
    {
        if (!_memory.TryGetValue(address.ToInt64(), out var bytes))
        {
            throw new InvalidOperationException($"Address 0x{address.ToInt64():X} not mapped.");
        }

        var length = Math.Min(maxLength, bytes.Length);
        var nullIndex = Array.IndexOf(bytes, (byte)0, 0, length);
        var count = nullIndex >= 0 ? nullIndex : length;
        return System.Text.Encoding.ASCII.GetString(bytes, 0, count);
    }

    public IntPtr ReadPointer(IntPtr address)
    {
        var value = Read<uint>(address);
        return new IntPtr(unchecked((long)value));
    }

    public IntPtr ResolveChain(params int[] offsets)
    {
        if (offsets.Length == 0)
        {
            throw new ArgumentException("At least one offset is required", nameof(offsets));
        }

        var current = IntPtr.Add(BaseAddress, offsets[0]);
        for (var i = 1; i < offsets.Length; i++)
        {
            current = ReadPointer(current);
            current = IntPtr.Add(current, offsets[i]);
        }

        return current;
    }

    public void Dispose()
    {
        _memory.Clear();
        IsAttached = false;
    }
}
