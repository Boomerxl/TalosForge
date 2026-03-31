using TalosForge.Core.Abstractions;
using TalosForge.Core.Models;

namespace TalosForge.Core.ObjectManager;

/// <summary>
/// Reads aura (buff/debuff) data from a WoW unit's aura table.
/// WoW 3.3.5a stores auras in a fixed-size table within the CGUnit structure.
/// </summary>
internal static class AuraReader
{
    public static IReadOnlyList<AuraInfo> ReadAuras(IMemoryReader reader, IntPtr unitPointer)
    {
        try
        {
            var auraCount = reader.Read<int>(IntPtr.Add(unitPointer, Offsets.UNIT_AURA_COUNT));
            if (auraCount <= 0 || auraCount > Offsets.MAX_AURAS)
                return Array.Empty<AuraInfo>();

            var tableBase = IntPtr.Add(unitPointer, Offsets.UNIT_AURA_TABLE_BASE);
            var tablePtr = reader.ReadPointer(tableBase);
            if (tablePtr == IntPtr.Zero || tablePtr.ToInt64() < 0x10000 || tablePtr.ToInt64() > 0x7FFFFFFF)
            {
                tablePtr = IntPtr.Add(unitPointer, Offsets.UNIT_AURA_TABLE_BASE + IntPtr.Size);
            }

            if (tablePtr == IntPtr.Zero || tablePtr.ToInt64() < 0x10000 || tablePtr.ToInt64() > 0x7FFFFFFF)
                return Array.Empty<AuraInfo>();

            var results = new List<AuraInfo>(auraCount);

            for (int i = 0; i < auraCount; i++)
            {
                var entryBase = IntPtr.Add(tablePtr, i * Offsets.AURA_ENTRY_SIZE);

                try
                {
                    var spellId = reader.Read<int>(IntPtr.Add(entryBase, Offsets.AURA_SPELL_ID));
                    if (spellId <= 0)
                        continue;

                    var casterGuid = reader.Read<ulong>(IntPtr.Add(entryBase, Offsets.AURA_CASTER_GUID));
                    var flags = reader.Read<byte>(IntPtr.Add(entryBase, Offsets.AURA_FLAGS));
                    var stacks = reader.Read<byte>(IntPtr.Add(entryBase, Offsets.AURA_STACKS));
                    var duration = reader.Read<int>(IntPtr.Add(entryBase, Offsets.AURA_DURATION));
                    var endTime = reader.Read<int>(IntPtr.Add(entryBase, Offsets.AURA_END_TIME));

                    results.Add(new AuraInfo(spellId, casterGuid, flags, stacks, duration, endTime));
                }
                catch
                {
                    break;
                }
            }

            return results;
        }
        catch
        {
            return Array.Empty<AuraInfo>();
        }
    }
}
