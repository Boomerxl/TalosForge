using System.Diagnostics;

namespace TalosForge.Core.Abstractions;

public interface IMemoryReader : IDisposable
{
    bool IsAttached { get; }
    IntPtr BaseAddress { get; }
    Process WowProcess { get; }

    bool Attach();
    void Detach();

    T Read<T>(IntPtr address) where T : struct;
    T ReadStruct<T>(IntPtr address) where T : struct;
    string ReadString(IntPtr address, int maxLength = 256);
    IntPtr ReadPointer(IntPtr address);
    IntPtr ResolveChain(params int[] offsets);
}
