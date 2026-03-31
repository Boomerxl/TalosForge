namespace TalosForge.Core.Models;

public sealed record WowObjectSnapshot(
    IntPtr Pointer,
    ulong Guid,
    int Type,
    Vector3 Position,
    float Facing,
    bool IsLocalPlayer,
    ulong? TargetGuid,
    uint? Health = null,
    uint? MaxHealth = null,
    uint? Mana = null,
    uint? MaxMana = null,
    int? Level = null,
    uint? EntryId = null,
    bool IsDead = false,
    uint? UnitFlags = null,
    uint? DynamicFlags = null,
    string? Name = null,
    int? FactionTemplate = null,
    IReadOnlyList<AuraInfo>? Auras = null);
