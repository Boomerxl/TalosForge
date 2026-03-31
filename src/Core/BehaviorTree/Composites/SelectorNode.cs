using TalosForge.Core.Plugins;

namespace TalosForge.Core.BehaviorTree.Composites;

/// <summary>
/// Tries each child in order until one succeeds or returns Running.
/// Returns Failure only if all children fail.
/// </summary>
public sealed class SelectorNode : IBtNode
{
    private readonly List<IBtNode> _children;
    private int _runningIndex = -1;

    public SelectorNode(string name, IEnumerable<IBtNode> children)
    {
        Name = name;
        _children = children.ToList();
    }

    public string Name { get; }

    public async Task<BtNodeStatus> TickAsync(IBotContext context, CancellationToken ct)
    {
        int start = _runningIndex >= 0 ? _runningIndex : 0;

        for (int i = start; i < _children.Count; i++)
        {
            var status = await _children[i].TickAsync(context, ct).ConfigureAwait(false);

            switch (status)
            {
                case BtNodeStatus.Success:
                    _runningIndex = -1;
                    return BtNodeStatus.Success;

                case BtNodeStatus.Running:
                    _runningIndex = i;
                    return BtNodeStatus.Running;
            }
        }

        _runningIndex = -1;
        return BtNodeStatus.Failure;
    }

    public void Reset()
    {
        _runningIndex = -1;
        foreach (var child in _children) child.Reset();
    }
}
