namespace TalosForge.Core;

/// <summary>
/// Shared pointer-chain resolution logic with explicit fail-fast validation.
/// </summary>
public static class PointerChainResolver
{
    public static IntPtr Resolve(IntPtr baseAddress, Func<IntPtr, IntPtr> readPointer, params int[] offsets)
    {
        ArgumentNullException.ThrowIfNull(readPointer);

        if (offsets == null || offsets.Length == 0)
        {
            throw new ArgumentException("At least one offset is required.", nameof(offsets));
        }

        if (baseAddress == IntPtr.Zero)
        {
            throw new InvalidOperationException("Base address is zero.");
        }

        var current = IntPtr.Add(baseAddress, offsets[0]);
        for (var i = 1; i < offsets.Length; i++)
        {
            current = readPointer(current);
            if (current == IntPtr.Zero)
            {
                throw new InvalidOperationException($"Pointer chain broke at depth {i}.");
            }

            current = IntPtr.Add(current, offsets[i]);
        }

        return current;
    }
}
