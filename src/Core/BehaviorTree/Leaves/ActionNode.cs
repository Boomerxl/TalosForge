using TalosForge.Core.Plugins;

namespace TalosForge.Core.BehaviorTree.Leaves;

/// <summary>
/// Executes an async action and returns its result.
/// </summary>
public sealed class ActionNode : IBtNode
{
    private readonly Func<IBotContext, CancellationToken, Task<BtNodeStatus>> _action;

    public ActionNode(string name, Func<IBotContext, CancellationToken, Task<BtNodeStatus>> action)
    {
        Name = name;
        _action = action;
    }

    public string Name { get; }

    public Task<BtNodeStatus> TickAsync(IBotContext context, CancellationToken ct)
        => _action(context, ct);

    public void Reset() { }
}
