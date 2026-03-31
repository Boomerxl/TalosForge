using TalosForge.Core.Plugins;

namespace TalosForge.Core.BehaviorTree.Leaves;

/// <summary>
/// Returns Running for a specified duration, then Success.
/// </summary>
public sealed class WaitNode : IBtNode
{
    private readonly TimeSpan _duration;
    private DateTimeOffset? _startedUtc;

    public WaitNode(string name, TimeSpan duration)
    {
        Name = name;
        _duration = duration;
    }

    public string Name { get; }

    public Task<BtNodeStatus> TickAsync(IBotContext context, CancellationToken ct)
    {
        _startedUtc ??= DateTimeOffset.UtcNow;

        if (DateTimeOffset.UtcNow - _startedUtc.Value >= _duration)
        {
            _startedUtc = null;
            return Task.FromResult(BtNodeStatus.Success);
        }

        return Task.FromResult(BtNodeStatus.Running);
    }

    public void Reset()
    {
        _startedUtc = null;
    }
}
