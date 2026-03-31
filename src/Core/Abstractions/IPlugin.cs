using TalosForge.Core.Models;
using TalosForge.Core.Plugins;

namespace TalosForge.Core.Abstractions;

public interface IPlugin : IDisposable
{
    string Name { get; }
    Version Version { get; }

    void Initialize(IPluginContext context);

    Task TickAsync(WorldSnapshot snapshot, IReadOnlyList<BotEvent> events, CancellationToken cancellationToken);

    /// <summary>
    /// Enhanced tick with full bot context. Override this for rich API access.
    /// Default implementation delegates to the legacy TickAsync.
    /// </summary>
    Task TickAsync(IBotContext context, IReadOnlyList<BotEvent> events, CancellationToken cancellationToken)
        => TickAsync(context.Snapshot, events, cancellationToken);

    PluginCapabilities Capabilities => PluginCapabilities.None;
}

[Flags]
public enum PluginCapabilities
{
    None = 0,
    Combat = 1,
    Navigation = 2,
    Gathering = 4,
    Grinding = 8,
    Fishing = 16,
}

public interface IPluginContext
{
    WorldSnapshot? LastSnapshot { get; }
    IReadOnlyList<BotEvent> LastEvents { get; }
    void QueueCommand(UnlockerCommand command);
}
