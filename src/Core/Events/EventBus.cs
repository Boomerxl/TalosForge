using TalosForge.Core.Abstractions;
using TalosForge.Core.Models;

namespace TalosForge.Core.Events;

/// <summary>
/// Emits typed events by diffing consecutive world snapshots.
/// </summary>
public sealed class EventBus : IEventBus
{
    private readonly object _syncRoot = new();
    private long _sequence;
    private WorldSnapshot? _previous;

    public IReadOnlyList<BotEvent> LastEvents { get; private set; } = Array.Empty<BotEvent>();

    public IReadOnlyList<BotEvent> ProcessSnapshot(WorldSnapshot snapshot)
    {
        lock (_syncRoot)
        {
            var events = new List<BotEvent>();
            var now = snapshot.TimestampUtc;

            if (_previous?.Player != null && snapshot.Player != null)
            {
                var previousPlayer = _previous.Player;
                var currentPlayer = snapshot.Player;

                if (!previousPlayer.InCombat && currentPlayer.InCombat)
                {
                    events.Add(new CombatStartedEvent(snapshot.TickId, NextSequence(), now));
                }
                else if (previousPlayer.InCombat && !currentPlayer.InCombat)
                {
                    events.Add(new CombatEndedEvent(snapshot.TickId, NextSequence(), now));
                }

                if (previousPlayer.TargetGuid != currentPlayer.TargetGuid)
                {
                    events.Add(new TargetChangedEvent(
                        snapshot.TickId,
                        NextSequence(),
                        now,
                        previousPlayer.TargetGuid,
                        currentPlayer.TargetGuid));
                }

                if (!previousPlayer.LootReady && currentPlayer.LootReady)
                {
                    events.Add(new LootAvailableEvent(snapshot.TickId, NextSequence(), now));
                }

                if (!previousPlayer.IsCasting && currentPlayer.IsCasting)
                {
                    events.Add(new CastStartedEvent(snapshot.TickId, NextSequence(), now));
                }
                else if (previousPlayer.IsCasting && !currentPlayer.IsCasting)
                {
                    events.Add(new CastEndedEvent(snapshot.TickId, NextSequence(), now));
                }
            }

            _previous = snapshot;
            LastEvents = events;
            return events;
        }
    }

    private long NextSequence()
    {
        _sequence++;
        return _sequence;
    }
}
