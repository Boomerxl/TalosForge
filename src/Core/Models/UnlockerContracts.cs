namespace TalosForge.Core.Models;

public enum UnlockerOpcode
{
    LuaDoString = 1,
    CastSpellByName = 2,
    SetTargetGuid = 3,
    Face = 4,
    MoveTo = 5,
    Interact = 6,
}

public sealed record UnlockerCommand(
    long CommandId,
    UnlockerOpcode Opcode,
    string PayloadJson,
    DateTimeOffset TimestampUtc);

public sealed record UnlockerAck(
    long CommandId,
    bool Success,
    string Message,
    string? PayloadJson,
    DateTimeOffset TimestampUtc);
