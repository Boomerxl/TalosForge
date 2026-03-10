using TalosForge.Core.Models;
using TalosForge.UnlockerHost.Abstractions;
using TalosForge.UnlockerHost.Models;

namespace TalosForge.UnlockerHost.Execution;

/// <summary>
/// Placeholder executor that accepts commands but reports not implemented.
/// </summary>
public sealed class NullCommandExecutor : ICommandExecutor
{
    public ValueTask<CommandExecutionResult> ExecuteAsync(UnlockerCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            CommandExecutionResult.Fail($"No executor implementation for opcode {command.Opcode}."));
    }
}
