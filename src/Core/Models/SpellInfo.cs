namespace TalosForge.Core.Models;

public sealed record SpellInfo(
    string Name,
    int SpellId,
    bool IsUsable,
    bool NoMana,
    float CooldownStartSec,
    float CooldownDurationSec,
    bool IsOnGcd);
