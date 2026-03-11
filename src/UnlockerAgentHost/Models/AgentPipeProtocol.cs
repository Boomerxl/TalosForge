namespace TalosForge.UnlockerAgentHost.Models;

public sealed record AgentPipeRequest(
    int Version,
    long CommandId,
    string Opcode,
    int OpcodeValue,
    string PayloadJson,
    long TimestampUnixMs,
    int RequestTimeoutMs,
    string? EvasionProfile);

public sealed record AgentPipeResponse(
    bool Success,
    string Message,
    string? PayloadJson,
    string? Code,
    string? DiagnosticsJson,
    string AgentState,
    long CompletedUnixMs);
