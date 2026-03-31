using TalosForge.Core.Plugins;

namespace TalosForge.Core.BehaviorTree.Composites;

/// <summary>
/// Executes each child in order until one fails or returns Running.
/// Returns Success only if all children succeed.
/// </summary>
public sealed class SequenceNode : IBtNode
{
    private readonly List<IBtNode> _children;
    private int _runningIndex = -1;

    public SequenceNode(string name, IEnumerable<IBtNode> children)
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
                case BtNodeStatus.Failure:
                    _runningIndex = -1;
                    return BtNodeStatus.Failure;

                case BtNodeStatus.Running:
                    _runningIndex = i;
                    return BtNodeStatus.Running;
            }
        }

        _runningIndex = -1;
        return BtNodeStatus.Success;
    }

    public void Reset()
    {
        _runningIndex = -1;
        foreach (var child in _children) child.Reset();
    }
}
