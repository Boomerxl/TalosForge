using TalosForge.Core.Plugins;

namespace TalosForge.Core.BehaviorTree;

public interface IBtNode
{
    string Name { get; }
    Task<BtNodeStatus> TickAsync(IBotContext context, CancellationToken ct);
    void Reset();
}
