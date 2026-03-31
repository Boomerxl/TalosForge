namespace TalosForge.Core.Models;

public sealed record AuraInfo(
    int SpellId,
    ulong CasterGuid,
    byte Flags,
    byte Stacks,
    int DurationMs,
    int EndTimeMs);
