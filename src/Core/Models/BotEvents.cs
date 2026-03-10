namespace TalosForge.Core.Models;

public abstract record BotEvent(
    long TickId,
    long Sequence,
    DateTimeOffset TimestampUtc,
    string EventName);

public sealed record CombatStartedEvent(long TickId, long Sequence, DateTimeOffset TimestampUtc)
    : BotEvent(TickId, Sequence, TimestampUtc, "CombatStarted");

public sealed record CombatEndedEvent(long TickId, long Sequence, DateTimeOffset TimestampUtc)
    : BotEvent(TickId, Sequence, TimestampUtc, "CombatEnded");

public sealed record TargetChangedEvent(
    long TickId,
    long Sequence,
    DateTimeOffset TimestampUtc,
    ulong? PreviousTargetGuid,
    ulong? CurrentTargetGuid)
    : BotEvent(TickId, Sequence, TimestampUtc, "TargetChanged");

public sealed record LootAvailableEvent(long TickId, long Sequence, DateTimeOffset TimestampUtc)
    : BotEvent(TickId, Sequence, TimestampUtc, "LootAvailable");

public sealed record CastStartedEvent(long TickId, long Sequence, DateTimeOffset TimestampUtc)
    : BotEvent(TickId, Sequence, TimestampUtc, "CastStarted");

public sealed record CastEndedEvent(long TickId, long Sequence, DateTimeOffset TimestampUtc)
    : BotEvent(TickId, Sequence, TimestampUtc, "CastEnded");
