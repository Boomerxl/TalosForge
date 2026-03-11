namespace TalosForge.UnlockerAgentHost.Runtime;

public interface IAgentRuntime : IAsyncDisposable
{
    ValueTask<AgentRuntimeReadyResult> EnsureReadyAsync(string evasionProfile, CancellationToken cancellationToken);
    ValueTask<AgentRuntimeExecutionResult> ExecuteAsync(AgentExecutionRequest request, CancellationToken cancellationToken);
}

public sealed record AgentRuntimeReadyResult(
    bool Success,
    string Message,
    string? Code = null);

public sealed record AgentExecutionRequest(
    long CommandId,
    string Opcode,
    string PayloadJson,
    int RequestTimeoutMs);

public sealed record AgentRuntimeExecutionResult(
    bool Success,
    string Message,
    string? PayloadJson,
    string Code,
    bool TransientFailure);
