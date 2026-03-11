# Shared-Memory IPC Contract v1

## Channels

- Command ring: `TalosForge.Cmd.v1`
- Event/ack ring: `TalosForge.Evt.v1`

## Ring Header (fixed, 20 bytes)

- `Magic` (int32)
- `Version` (int32)
- `Capacity` (int32)
- `WriteIndex` (int32)
- `ReadIndex` (int32)

## Frame Format

Each ring payload frame is encoded as:

- `PayloadLength` (int32)
- `PayloadBytes` (byte[])

## Command Payload Envelope

- `CommandId` (int64)
- `Opcode` (int32)
- `PayloadLength` (int32)
- `TimestampUnixMs` (int64)
- `PayloadJson` (UTF-8 bytes)

Opcodes:
- `LuaDoString`
- `CastSpellByName`
- `SetTargetGuid`
- `Face`
- `MoveTo`
- `Interact`
- `Stop`

## Ack Payload Envelope

- `CommandId` (int64)
- `Success` (int32: 1/0)
- `PayloadLength` (int32)
- `TimestampUnixMs` (int64)
- `MessageLength` (int32)
- `Message` (UTF-8 bytes)
- `PayloadJson` (UTF-8 bytes)

## Reliability

- Client uses timeout + retry.
- Ack correlation is by `CommandId`.
- Ring header corruption resets indices.

## Reference Endpoint

- `src/UnlockerHost/TalosForge.UnlockerHost.csproj` provides a standalone endpoint implementation for this contract.
- Endpoint heartbeat status file (default): `%TEMP%/TalosForge.UnlockerHost.status.json`.
