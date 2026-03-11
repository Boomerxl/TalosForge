using TalosForge.Core.Models;
using TalosForge.UnlockerHost.Models;

namespace TalosForge.UnlockerHost.Abstractions;

/// <summary>
/// Backend adapter abstraction for validated unlocker commands.
/// </summary>
public interface IAdapterBackend
{
    ValueTask<CommandExecutionResult> ExecuteAsync(UnlockerCommand command, CancellationToken cancellationToken);
}
