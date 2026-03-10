using TalosForge.Core.Models;

namespace TalosForge.Core.Abstractions;

public interface IPlugin : IDisposable
{
    string Name { get; }
    Version Version { get; }
    void Initialize(IPluginContext context);
    Task TickAsync(WorldSnapshot snapshot, IReadOnlyList<BotEvent> events, CancellationToken cancellationToken);
}

public interface IPluginContext
{
    WorldSnapshot? LastSnapshot { get; }
    IReadOnlyList<BotEvent> LastEvents { get; }
    void QueueCommand(UnlockerCommand command);
}
