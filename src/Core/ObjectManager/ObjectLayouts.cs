using System.Runtime.InteropServices;

namespace TalosForge.Core.ObjectManager;

/// <summary>
/// 3.3.5a (12340) object layouts mirrored from Binana's generated profile scripts.
/// All offsets are absolute within each object instance.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0xD0)]
internal struct CGObject
{
    [FieldOffset(0x00)] public IntPtr VTablePtr;
    [FieldOffset(0x08)] public IntPtr DataBeginPtr;
    [FieldOffset(0x0C)] public IntPtr DataEndPtr;
    [FieldOffset(0x10)] public uint Flags;
    [FieldOffset(0x14)] public int TypeId;
    [FieldOffset(0x18)] public uint LowGuid;
    [FieldOffset(0x30)] public ulong Guid;

    // Binana traversal tooling and legacy object manager traversals both use +0x3C for the next object link.
    [FieldOffset(0x3C)] public IntPtr NextObjectPtr;

    [FieldOffset(0x98)] public float RenderScale1;
    [FieldOffset(0x9C)] public float RenderScale2;
    [FieldOffset(0xA0)] public int ScalingEndMs;
    [FieldOffset(0xA4)] public float LastScale;
    [FieldOffset(0xA8)] public IntPtr SpecialEffectPtr;
    [FieldOffset(0xAC)] public float ObjectHeight;
    [FieldOffset(0xB0)] public IntPtr NamePtr;
    [FieldOffset(0xB4)] public IntPtr ModelPtr;
    [FieldOffset(0xB8)] public IntPtr MapEntityPtr;
    [FieldOffset(0xBC)] public uint MovementFlags;
    [FieldOffset(0xC8)] public byte Alpha;
    [FieldOffset(0xC9)] public byte StartAlpha;
    [FieldOffset(0xCA)] public byte EndAlpha;
    [FieldOffset(0xCB)] public byte MaxAlpha;
    [FieldOffset(0xCC)] public IntPtr EffectManagerPtr;
}

[StructLayout(LayoutKind.Explicit, Size = 0x1440)]
internal struct CGUnit
{
    [FieldOffset(0x00)] public CGObject Base;
    [FieldOffset(0x0D0)] public IntPtr UnitDataPtr;
    [FieldOffset(0x0D4)] public IntPtr UnknownD4;
    [FieldOffset(0x0D8)] public IntPtr MovementDataPtr;

    // Embedded movement block starts at 0x788 in Binana's CGUnit layout.
    [FieldOffset(0x798)] public float PositionY;
    [FieldOffset(0x79C)] public float PositionX;
    [FieldOffset(0x7A0)] public float PositionZ;
    [FieldOffset(0x7A8)] public float Facing;
    [FieldOffset(0x7AC)] public float Pitch;

    [FieldOffset(0x0A78)] public int SpellCastStartMs;
    [FieldOffset(0x0A7C)] public int SpellCastEndMs;
    [FieldOffset(0x0BEC)] public int CombatFlag;

    [FieldOffset(0x0DD0)] public int AuraCount;
    [FieldOffset(0x0E54)] public int AuraSortedCount;

    [FieldOffset(0x1068)] public uint Health;
    [FieldOffset(0x106C)] public uint Mana;
    [FieldOffset(0x1088)] public uint MaxHealth;
    [FieldOffset(0x108C)] public uint MaxMana;
    [FieldOffset(0x1100)] public float MaxInteractDistance;
}

[StructLayout(LayoutKind.Explicit, Size = 0x2E18)]
internal struct CGPlayer
{
    [FieldOffset(0x00)] public CGUnit Base;

    [FieldOffset(0x18E0)] public ulong LootTargetGuid;
    [FieldOffset(0x18F4)] public IntPtr PlayerInventoryPtr;
    [FieldOffset(0x1938)] public ulong LastCombatUnitGuid;
    [FieldOffset(0x194C)] public int TotalPlayedSeconds;

    // ObjectFields embed at 0x1958.
    [FieldOffset(0x1958)] public ulong DescriptorGuid;
    [FieldOffset(0x1960)] public uint DescriptorType;
    [FieldOffset(0x1968)] public float DescriptorScale;

    // UnitFields embed at 0x1970.
    [FieldOffset(0x19A0)] public ulong TargetGuid;
    [FieldOffset(0x19B8)] public int DescriptorHealth;
    [FieldOffset(0x19BC)] public int DescriptorMana;
    [FieldOffset(0x19D8)] public int DescriptorMaxHealth;
    [FieldOffset(0x19DC)] public int DescriptorMaxMana;
    [FieldOffset(0x1A30)] public int Level;
    [FieldOffset(0x1A94)] public int DynFlags;
    [FieldOffset(0x1AA0)] public int NpcFlags;

    // PlayerFields embed at 0x1BB0.
    [FieldOffset(0x2340)] public int Xp;
    [FieldOffset(0x2344)] public int NextLevelXp;
    [FieldOffset(0x2BA0)] public int Money;
}

[StructLayout(LayoutKind.Explicit, Size = 0x5A8)]
internal struct CGItem
{
    [FieldOffset(0x00)] public CGObject Base;

    // ObjectFields embed at 0x3E0.
    [FieldOffset(0x3E0)] public ulong DescriptorGuid;
    [FieldOffset(0x3E8)] public uint DescriptorType;
    [FieldOffset(0x3F0)] public float DescriptorScale;

    // ItemFields embed at 0x3F8.
    [FieldOffset(0x3F8)] public ulong OwnerGuid;
    [FieldOffset(0x400)] public ulong ContainedGuid;
    [FieldOffset(0x408)] public ulong CreatorGuid;
    [FieldOffset(0x410)] public ulong GiftCreatorGuid;
    [FieldOffset(0x418)] public int DescriptorStackCount;
    [FieldOffset(0x41C)] public int DurationMs;

    [FieldOffset(0x4E0)] public int ItemId;
    [FieldOffset(0x4E4)] public float ItemScale;
    [FieldOffset(0x4F4)] public int StackCount;
    [FieldOffset(0x50C)] public uint ItemFlags;
    [FieldOffset(0x5A0)] public int Durability;
}
