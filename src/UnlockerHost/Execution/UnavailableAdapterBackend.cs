using TalosForge.Core.Models;
using TalosForge.UnlockerHost.Abstractions;
using TalosForge.UnlockerHost.Models;

namespace TalosForge.UnlockerHost.Execution;

/// <summary>
/// Placeholder adapter backend until a real unlocker bridge is wired.
/// </summary>
public sealed class UnavailableAdapterBackend : IAdapterBackend
{
    public ValueTask<CommandExecutionResult> ExecuteAsync(UnlockerCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            CommandExecutionResult.Fail(
                $"{AdapterResultCodes.BackendUnavailable}: No unlocker adapter backend is configured.",
                AdapterCommandExecutor.BuildCodePayload(
                    AdapterResultCodes.BackendUnavailable,
                    "No unlocker adapter backend is configured.")));
    }
}
