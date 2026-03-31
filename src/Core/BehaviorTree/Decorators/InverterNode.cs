using TalosForge.Core.Plugins;

namespace TalosForge.Core.BehaviorTree.Decorators;

/// <summary>
/// Inverts the result of its child: Success becomes Failure and vice versa.
/// Running passes through unchanged.
/// </summary>
public sealed class InverterNode : IBtNode
{
    private readonly IBtNode _child;

    public InverterNode(string name, IBtNode child)
    {
        Name = name;
        _child = child;
    }

    public string Name { get; }

    public async Task<BtNodeStatus> TickAsync(IBotContext context, CancellationToken ct)
    {
        var status = await _child.TickAsync(context, ct).ConfigureAwait(false);
        return status switch
        {
            BtNodeStatus.Success => BtNodeStatus.Failure,
            BtNodeStatus.Failure => BtNodeStatus.Success,
            _ => status,
        };
    }

    public void Reset() => _child.Reset();
}
