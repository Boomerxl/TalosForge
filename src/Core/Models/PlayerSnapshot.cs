namespace TalosForge.Core.Models;

public sealed record PlayerSnapshot(
    ulong Guid,
    Vector3 Position,
    float Facing,
    ulong? TargetGuid,
    bool InCombat,
    bool IsCasting,
    bool LootReady,
    bool IsMoving,
    int? Health = null,
    int? MaxHealth = null);
