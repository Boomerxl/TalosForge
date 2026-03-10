using System.Collections.Concurrent;
using TalosForge.Core.Abstractions;
using TalosForge.Core.Models;

namespace TalosForge.Core.Plugins;

internal sealed class PluginRuntimeContext : IPluginContext
{
    private readonly ConcurrentQueue<UnlockerCommand> _queue = new();

    public WorldSnapshot? LastSnapshot { get; private set; }
    public IReadOnlyList<BotEvent> LastEvents { get; private set; } = Array.Empty<BotEvent>();

    public void SetTickContext(WorldSnapshot snapshot, IReadOnlyList<BotEvent> events)
    {
        LastSnapshot = snapshot;
        LastEvents = events;
    }

    public void QueueCommand(UnlockerCommand command)
    {
        _queue.Enqueue(command);
    }

    public bool TryDequeue(out UnlockerCommand command)
    {
        return _queue.TryDequeue(out command!);
    }
}
