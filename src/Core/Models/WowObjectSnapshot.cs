namespace TalosForge.Core.Models;

public sealed record WowObjectSnapshot(
    IntPtr Pointer,
    ulong Guid,
    int Type,
    Vector3 Position,
    float Facing,
    bool IsLocalPlayer,
    ulong? TargetGuid);
