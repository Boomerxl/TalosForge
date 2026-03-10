using Microsoft.Extensions.Logging.Abstractions;
using TalosForge.Core.Abstractions;
using TalosForge.Core.Bot;
using TalosForge.Core.Configuration;
using TalosForge.Core.IPC;
using TalosForge.Core.Models;
using Xunit;

namespace TalosForge.Tests.Bot;

public sealed class BotEngineTests
{
    [Fact]
    public void Scheduler_Selects_Combat_Interval_And_Clamps()
    {
        var options = new BotOptions
        {
            CombatTickMs = 35,
            MovementTickMs = 70,
            IdleTickMs = 120,
            MinTickMs = 25,
            MaxTickMs = 150,
        };

        var engine = new BotEngine(
            new StubObjectManager(),
            new StubEventBus(),
            new NullUnlockerClient(),
            options,
            NullLogger<BotEngine>.Instance);

        var interval = engine.ComputeTickIntervalMs(BotState.Combat, Array.Empty<BotEvent>());
        Assert.Equal(35, interval);

        var highPressure = new List<BotEvent>
        {
            new CombatStartedEvent(1, 1, DateTimeOffset.UtcNow),
            new TargetChangedEvent(1, 2, DateTimeOffset.UtcNow, null, 1),
            new LootAvailableEvent(1, 3, DateTimeOffset.UtcNow),
            new CastStartedEvent(1, 4, DateTimeOffset.UtcNow),
        };

        var adaptive = engine.ComputeTickIntervalMs(BotState.Combat, highPressure);
        Assert.Equal(30, adaptive);
    }

    [Fact]
    public void Scheduler_Clamps_To_Min_And_Max()
    {
        var options = new BotOptions
        {
            CombatTickMs = 10,
            MovementTickMs = 999,
            IdleTickMs = 999,
            MinTickMs = 25,
            MaxTickMs = 150,
        };

        var engine = new BotEngine(
            new StubObjectManager(),
            new StubEventBus(),
            new NullUnlockerClient(),
            options,
            NullLogger<BotEngine>.Instance);

        Assert.Equal(25, engine.ComputeTickIntervalMs(BotState.Combat, Array.Empty<BotEvent>()));
        Assert.Equal(150, engine.ComputeTickIntervalMs(BotState.Movement, Array.Empty<BotEvent>()));
    }

    private sealed class StubObjectManager : IObjectManager
    {
        public WorldSnapshot GetSnapshot(long tickId)
        {
            return new WorldSnapshot(
                tickId,
                DateTimeOffset.UtcNow,
                Array.Empty<WowObjectSnapshot>(),
                new PlayerSnapshot(1, new Vector3(0, 0, 0), 0, null, false, false, false, false),
                true,
                null);
        }
    }

    private sealed class StubEventBus : IEventBus
    {
        public IReadOnlyList<BotEvent> LastEvents { get; private set; } = Array.Empty<BotEvent>();

        public IReadOnlyList<BotEvent> ProcessSnapshot(WorldSnapshot snapshot)
        {
            LastEvents = Array.Empty<BotEvent>();
            return LastEvents;
        }
    }
}
