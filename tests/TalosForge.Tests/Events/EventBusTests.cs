using TalosForge.Core.Events;
using TalosForge.Core.Models;
using Xunit;

namespace TalosForge.Tests.Events;

public sealed class EventBusTests
{
    [Fact]
    public void Emits_Expected_Events_On_State_Transitions()
    {
        var bus = new EventBus();

        var snapshot1 = new WorldSnapshot(
            1,
            DateTimeOffset.UtcNow,
            Array.Empty<WowObjectSnapshot>(),
            new PlayerSnapshot(1, new Vector3(0, 0, 0), 0, null, false, false, false, false),
            true,
            null);

        var snapshot2 = new WorldSnapshot(
            2,
            DateTimeOffset.UtcNow.AddMilliseconds(50),
            Array.Empty<WowObjectSnapshot>(),
            new PlayerSnapshot(1, new Vector3(0, 0, 0), 0, 55, true, true, true, false),
            true,
            null);

        var snapshot3 = new WorldSnapshot(
            3,
            DateTimeOffset.UtcNow.AddMilliseconds(100),
            Array.Empty<WowObjectSnapshot>(),
            new PlayerSnapshot(1, new Vector3(0, 0, 0), 0, null, false, false, true, false),
            true,
            null);

        Assert.Empty(bus.ProcessSnapshot(snapshot1));

        var eventsAt2 = bus.ProcessSnapshot(snapshot2);
        Assert.Equal(4, eventsAt2.Count);
        Assert.IsType<CombatStartedEvent>(eventsAt2[0]);
        Assert.IsType<TargetChangedEvent>(eventsAt2[1]);
        Assert.IsType<LootAvailableEvent>(eventsAt2[2]);
        Assert.IsType<CastStartedEvent>(eventsAt2[3]);

        var eventsAt3 = bus.ProcessSnapshot(snapshot3);
        Assert.Equal(3, eventsAt3.Count);
        Assert.IsType<CombatEndedEvent>(eventsAt3[0]);
        Assert.IsType<TargetChangedEvent>(eventsAt3[1]);
        Assert.IsType<CastEndedEvent>(eventsAt3[2]);

        Assert.True(eventsAt3[0].Sequence > eventsAt2[3].Sequence);
    }
}
