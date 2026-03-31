using Microsoft.Extensions.Logging;
using TalosForge.Core.Plugins;

namespace TalosForge.Core.BehaviorTree;

/// <summary>
/// Executes a behavior tree each tick, managing the root node lifecycle.
/// </summary>
public sealed class BehaviorTreeRunner
{
    private readonly IBtNode _root;
    private readonly ILogger? _logger;
    private BtNodeStatus _lastStatus = BtNodeStatus.Success;

    public BehaviorTreeRunner(IBtNode root, ILogger? logger = null)
    {
        _root = root;
        _logger = logger;
    }

    public BtNodeStatus LastStatus => _lastStatus;
    public string RootName => _root.Name;

    public async Task<BtNodeStatus> TickAsync(IBotContext context, CancellationToken ct)
    {
        try
        {
            _lastStatus = await _root.TickAsync(context, ct).ConfigureAwait(false);

            if (_lastStatus != BtNodeStatus.Running)
            {
                _root.Reset();
            }

            return _lastStatus;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Behavior tree '{TreeName}' tick failed.", _root.Name);
            _lastStatus = BtNodeStatus.Failure;
            _root.Reset();
            return BtNodeStatus.Failure;
        }
    }

    public void Reset()
    {
        _root.Reset();
        _lastStatus = BtNodeStatus.Success;
    }
}
