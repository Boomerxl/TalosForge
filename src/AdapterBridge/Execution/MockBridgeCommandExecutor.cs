using TalosForge.AdapterBridge.Models;

namespace TalosForge.AdapterBridge.Execution;

public sealed class MockBridgeCommandExecutor : IBridgeCommandExecutor
{
    public ValueTask<AdapterPipeResponse> ExecuteAsync(AdapterPipeRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var opcode = string.IsNullOrWhiteSpace(request.Opcode)
            ? $"Unknown:{request.OpcodeValue}"
            : request.Opcode.Trim();

        return ValueTask.FromResult(new AdapterPipeResponse(
            Success: true,
            Message: $"ACK:{opcode}",
            PayloadJson: request.PayloadJson,
            Code: "OK"));
    }
}
