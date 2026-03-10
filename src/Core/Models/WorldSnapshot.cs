namespace TalosForge.Core.Models;

public sealed record WorldSnapshot(
    long TickId,
    DateTimeOffset TimestampUtc,
    IReadOnlyList<WowObjectSnapshot> Objects,
    PlayerSnapshot? Player,
    bool Success,
    string? ErrorMessage)
{
    public static WorldSnapshot Empty(long tickId, string errorMessage)
    {
        return new WorldSnapshot(
            tickId,
            DateTimeOffset.UtcNow,
            Array.Empty<WowObjectSnapshot>(),
            null,
            false,
            errorMessage);
    }
}
