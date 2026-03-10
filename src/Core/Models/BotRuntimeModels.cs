namespace TalosForge.Core.Models;

public sealed record BotTickMetrics(
    long TickId,
    int TickMs,
    int SnapshotMs,
    int EventsCount,
    int CommandsCount,
    DateTimeOffset TimestampUtc);

public enum BotState
{
    Idle = 0,
    Combat = 1,
    Movement = 2,
}
