# Unlocker Host

## Purpose

`TalosForge.UnlockerHost` is a standalone shared-memory endpoint that receives commands from Core and writes correlated ACK responses.

It provides a stable process boundary for command execution without coupling bot logic to transport internals.

## Run

Start host:

```powershell
dotnet run --project C:/Utilities/TalosForge/src/UnlockerHost/TalosForge.UnlockerHost.csproj -c Release -- --executor mock
```

Start Core against host:

```powershell
dotnet run --project C:/Utilities/TalosForge/src/Core/TalosForge.Core.csproj -c Release -- --real-unlocker
```

## CLI options

- `--command-ring` (`TalosForge.Cmd.v1` default)
- `--event-ring` (`TalosForge.Evt.v1` default)
- `--ring-capacity` (bytes)
- `--executor mock|null`
- `--poll-ms`
- `--ack-retries`
- `--ack-delay-ms`
- `--stats-interval`
- `--status-file`
- `--status-interval-ms`
- `--smoke`
- `--smoke-seconds`

## Behavior

- Reads command frames from command ring.
- Decodes `UnlockerCommand` envelope.
- Executes through configured `ICommandExecutor`.
- Writes `UnlockerAck` frame with matching `CommandId`.
- Retries ACK write on temporary ring-full conditions.
- Emits periodic host stats logs.
- Writes a heartbeat status file (`%TEMP%/TalosForge.UnlockerHost.status.json` by default).
