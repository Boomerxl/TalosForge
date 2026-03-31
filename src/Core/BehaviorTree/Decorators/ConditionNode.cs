using TalosForge.Core.Plugins;

namespace TalosForge.Core.BehaviorTree.Decorators;

/// <summary>
/// Only ticks its child if a condition is met. Returns Failure if condition is false.
/// </summary>
public sealed class ConditionNode : IBtNode
{
    private readonly IBtNode _child;
    private readonly Func<IBotContext, bool> _condition;

    public ConditionNode(string name, Func<IBotContext, bool> condition, IBtNode child)
    {
        Name = name;
        _condition = condition;
        _child = child;
    }

    public string Name { get; }

    public Task<BtNodeStatus> TickAsync(IBotContext context, CancellationToken ct)
    {
        if (!_condition(context))
            return Task.FromResult(BtNodeStatus.Failure);

        return _child.TickAsync(context, ct);
    }

    public void Reset() => _child.Reset();
}
