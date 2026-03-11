using TalosForge.Core.Models;
using TalosForge.UnlockerHost.Abstractions;
using TalosForge.UnlockerHost.Models;

namespace TalosForge.UnlockerHost.Execution;

/// <summary>
/// Deterministic executor used for local integration tests and IPC validation.
/// </summary>
public sealed class MockCommandExecutor : ICommandExecutor
{
    public ValueTask<CommandExecutionResult> ExecuteAsync(UnlockerCommand command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var message = command.Opcode switch
        {
            UnlockerOpcode.LuaDoString => "ACK:LuaDoString",
            UnlockerOpcode.CastSpellByName => "ACK:CastSpellByName",
            UnlockerOpcode.SetTargetGuid => "ACK:SetTargetGuid",
            UnlockerOpcode.Face => "ACK:Face",
            UnlockerOpcode.MoveTo => "ACK:MoveTo",
            UnlockerOpcode.Interact => "ACK:Interact",
            UnlockerOpcode.Stop => "ACK:Stop",
            _ => $"ACK:Unknown:{(int)command.Opcode}",
        };

        // Echo payload so callers can validate end-to-end framing/correlation.
        return ValueTask.FromResult(CommandExecutionResult.Ok(message, command.PayloadJson));
    }
}
