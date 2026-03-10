using TalosForge.Core.Models;
using TalosForge.UnlockerHost.Models;

namespace TalosForge.UnlockerHost.Abstractions;

/// <summary>
/// Executes a decoded unlocker command and returns an execution result.
/// </summary>
public interface ICommandExecutor
{
    ValueTask<CommandExecutionResult> ExecuteAsync(UnlockerCommand command, CancellationToken cancellationToken);
}
