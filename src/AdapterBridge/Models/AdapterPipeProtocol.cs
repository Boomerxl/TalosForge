namespace TalosForge.AdapterBridge.Models;

public sealed record AdapterPipeRequest(
    int Version,
    long CommandId,
    string Opcode,
    int OpcodeValue,
    string PayloadJson,
    long TimestampUnixMs);

public sealed record AdapterPipeResponse(
    bool Success,
    string Message,
    string? PayloadJson = null,
    string? Code = null);
