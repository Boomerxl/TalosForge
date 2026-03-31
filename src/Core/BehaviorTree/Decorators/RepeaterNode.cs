using TalosForge.Core.Plugins;

namespace TalosForge.Core.BehaviorTree.Decorators;

/// <summary>
/// Repeats its child a specified number of times (or indefinitely if maxRepeats &lt;= 0).
/// Returns Running until all repetitions complete with Success.
/// Returns Failure immediately if the child fails.
/// </summary>
public sealed class RepeaterNode : IBtNode
{
    private readonly IBtNode _child;
    private readonly int _maxRepeats;
    private int _currentRepeat;

    public RepeaterNode(string name, IBtNode child, int maxRepeats = 0)
    {
        Name = name;
        _child = child;
        _maxRepeats = maxRepeats;
    }

    public string Name { get; }

    public async Task<BtNodeStatus> TickAsync(IBotContext context, CancellationToken ct)
    {
        var status = await _child.TickAsync(context, ct).ConfigureAwait(false);

        if (status == BtNodeStatus.Running)
            return BtNodeStatus.Running;

        if (status == BtNodeStatus.Failure)
        {
            _currentRepeat = 0;
            return BtNodeStatus.Failure;
        }

        _currentRepeat++;
        if (_maxRepeats > 0 && _currentRepeat >= _maxRepeats)
        {
            _currentRepeat = 0;
            return BtNodeStatus.Success;
        }

        _child.Reset();
        return BtNodeStatus.Running;
    }

    public void Reset()
    {
        _currentRepeat = 0;
        _child.Reset();
    }
}
