using TalosForge.Core.Plugins;

namespace TalosForge.Core.BehaviorTree.Leaves;

/// <summary>
/// Evaluates a synchronous condition. Returns Success if true, Failure if false.
/// </summary>
public sealed class CheckNode : IBtNode
{
    private readonly Func<IBotContext, bool> _predicate;

    public CheckNode(string name, Func<IBotContext, bool> predicate)
    {
        Name = name;
        _predicate = predicate;
    }

    public string Name { get; }

    public Task<BtNodeStatus> TickAsync(IBotContext context, CancellationToken ct)
        => Task.FromResult(_predicate(context) ? BtNodeStatus.Success : BtNodeStatus.Failure);

    public void Reset() { }
}
