using TalosForge.Core.Plugins;

namespace TalosForge.Core.BehaviorTree.Composites;

/// <summary>
/// Ticks all children every frame. Succeeds when the required number succeed,
/// fails when enough fail to make the threshold impossible.
/// </summary>
public sealed class ParallelNode : IBtNode
{
    private readonly List<IBtNode> _children;
    private readonly int _successThreshold;

    public ParallelNode(string name, IEnumerable<IBtNode> children, int successThreshold = -1)
    {
        Name = name;
        _children = children.ToList();
        _successThreshold = successThreshold > 0 ? successThreshold : _children.Count;
    }

    public string Name { get; }

    public async Task<BtNodeStatus> TickAsync(IBotContext context, CancellationToken ct)
    {
        int successes = 0, failures = 0;

        foreach (var child in _children)
        {
            var status = await child.TickAsync(context, ct).ConfigureAwait(false);
            if (status == BtNodeStatus.Success) successes++;
            else if (status == BtNodeStatus.Failure) failures++;
        }

        if (successes >= _successThreshold) return BtNodeStatus.Success;
        if (failures > _children.Count - _successThreshold) return BtNodeStatus.Failure;
        return BtNodeStatus.Running;
    }

    public void Reset()
    {
        foreach (var child in _children) child.Reset();
    }
}
