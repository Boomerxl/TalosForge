using TalosForge.Core.Plugins;

namespace TalosForge.Core.BehaviorTree.Decorators;

/// <summary>
/// Prevents its child from executing more than once per cooldown period.
/// Returns Failure during cooldown, delegates to child otherwise.
/// </summary>
public sealed class CooldownNode : IBtNode
{
    private readonly IBtNode _child;
    private readonly TimeSpan _cooldown;
    private DateTimeOffset _lastExecutionUtc = DateTimeOffset.MinValue;

    public CooldownNode(string name, IBtNode child, TimeSpan cooldown)
    {
        Name = name;
        _child = child;
        _cooldown = cooldown;
    }

    public string Name { get; }

    public async Task<BtNodeStatus> TickAsync(IBotContext context, CancellationToken ct)
    {
        if (DateTimeOffset.UtcNow - _lastExecutionUtc < _cooldown)
            return BtNodeStatus.Failure;

        var status = await _child.TickAsync(context, ct).ConfigureAwait(false);

        if (status == BtNodeStatus.Success)
            _lastExecutionUtc = DateTimeOffset.UtcNow;

        return status;
    }

    public void Reset()
    {
        _lastExecutionUtc = DateTimeOffset.MinValue;
        _child.Reset();
    }
}
