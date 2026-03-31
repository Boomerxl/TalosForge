# Shared-Memory IPC Contract (v1)

## Rings

- Command ring: `TalosForge.Cmd.v1`
- Event/ACK ring: `TalosForge.Evt.v1`

Both rings use the shared-memory frame format implemented by:

- `src/Core/IPC/SharedMemoryUnlockerClient.cs`
- `src/UnlockerHost/Host/UnlockerHostService.cs`

## Command envelope

- `CommandId` (int64)
- `Opcode` (int32)
- `PayloadJson` (UTF-8 JSON)
- `TimestampUnixMs` (int64)

Representative opcodes:

- `LuaDoString`
- `LuaQuery`
- `CastSpellByName`
- `SetTargetGuid`
- `Face`
- `MoveTo`
- `Interact`
- `Stop`

## ACK envelope

- `CommandId` (int64)
- `Success` (bool/int)
- `Message` (string)
- `PayloadJson` (optional JSON string)
- `TimestampUtc` / unix timestamp (implementation-specific model projection)

## Reliability model

- Correlation is by `CommandId`.
- Client timeout/retry policy is handled in Core options.
- Host performs bounded ACK write retries when event ring is temporarily full.

## Session metadata

`session_id` is a process/log trace context only (CLI/env metadata).  
It is intentionally **not** a required field in IPC payloads for v1, so protocol compatibility is unchanged.

## Related runtime status files

- Host heartbeat file default: `%TEMP%/TalosForge.UnlockerHost.status.json`
- Supervisor stack status JSON: `scripts/supervisor.ps1 -Action status -Json`
