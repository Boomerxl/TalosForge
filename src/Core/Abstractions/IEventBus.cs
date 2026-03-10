using TalosForge.Core.Models;

namespace TalosForge.Core.Abstractions;

public interface IEventBus
{
    IReadOnlyList<BotEvent> ProcessSnapshot(WorldSnapshot snapshot);
    IReadOnlyList<BotEvent> LastEvents { get; }
}
