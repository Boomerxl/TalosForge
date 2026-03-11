# Unlocker Host

## Purpose

`TalosForge.UnlockerHost` is a standalone shared-memory endpoint that receives commands from Core and writes correlated ACK responses.

It provides a stable process boundary for command execution without coupling bot logic to transport internals.

## Run

Start host:

```powershell
dotnet run --project C:/Utilities/TalosForge/src/UnlockerHost/TalosForge.UnlockerHost.csproj -c Release -- --executor mock
```

Start host in adapter+pipe mode:

```powershell
dotnet run --project C:/Utilities/TalosForge/src/UnlockerHost/TalosForge.UnlockerHost.csproj -c Release -- --executor adapter --adapter-backend pipe --adapter-pipe TalosForge.UnlockerAdapter.v1
```

Start AdapterBridge (mock mode) for adapter backend validation:

```powershell
dotnet run --project C:/Utilities/TalosForge/src/AdapterBridge/TalosForge.AdapterBridge.csproj -c Release -- --mode mock --pipe-name TalosForge.UnlockerAdapter.v1
```

Start AdapterBridge in wow-cli mode:

```powershell
dotnet run --project C:/Utilities/TalosForge/src/AdapterBridge/TalosForge.AdapterBridge.csproj -c Release -- --mode wow-cli --pipe-name TalosForge.UnlockerAdapter.v1 --command-path C:/path/to/unlocker-cli.exe
```

Start UnlockerAgentHost + AdapterBridge in wow-agent mode (recommended):

```powershell
dotnet run --project C:/Utilities/TalosForge/src/UnlockerAgentHost/TalosForge.UnlockerAgentHost.csproj -c Release -- --pipe-name TalosForge.Agent.v1 --evasion-profile full
dotnet run --project C:/Utilities/TalosForge/src/AdapterBridge/TalosForge.AdapterBridge.csproj -c Release -- --mode wow-agent --pipe-name TalosForge.UnlockerAdapter.v1 --agent-pipe TalosForge.Agent.v1
```

Start Core against host:

```powershell
dotnet run --project C:/Utilities/TalosForge/src/Core/TalosForge.Core.csproj -c Release -- --real-unlocker
```

## CLI options

- `--command-ring` (`TalosForge.Cmd.v1` default)
- `--event-ring` (`TalosForge.Evt.v1` default)
- `--ring-capacity` (bytes)
- `--executor mock|null|adapter`
- `--poll-ms`
- `--ack-retries`
- `--ack-delay-ms`
- `--stats-interval`
- `--status-file`
- `--status-interval-ms`
- `--adapter-backend` (`pipe|unavailable`, default `pipe`)
- `--adapter-pipe` (named pipe for adapter bridge, default `TalosForge.UnlockerAdapter.v1`)
- `--adapter-connect-timeout-ms` (default `1200`)
- `--adapter-request-timeout-ms` (default `2500`)
- `--smoke`
- `--smoke-seconds`

## Behavior

- Reads command frames from command ring.
- Decodes `UnlockerCommand` envelope.
- Executes through configured `ICommandExecutor`.
  - `adapter` mode performs strict opcode payload validation and returns structured result codes.
  - `adapter` + `pipe` backend forwards validated commands to a local named-pipe bridge process.
- Writes `UnlockerAck` frame with matching `CommandId`.
- Retries ACK write on temporary ring-full conditions.
- Emits periodic host stats logs.
- Writes a heartbeat status file (`%TEMP%/TalosForge.UnlockerHost.status.json` by default).
- Preserves bridge-provided structured payloads. If bridge returns a `code` without `payloadJson`, host synthesizes a standard payload:
  - `{"code":"<code>","message":"<message>"}`

## Adapter pipe protocol (v1)
When running with `--executor adapter --adapter-backend pipe`, each command is sent as one JSON line:

```json
{"version":1,"commandId":123,"opcode":"LuaDoString","opcodeValue":1,"payloadJson":"{\"code\":\"print('hi')\"}","timestampUnixMs":1730842800000}
```

The bridge must respond with one JSON line:

```json
{"success":true,"message":"ACK:LuaDoString","payloadJson":"{\"bridge\":true}","code":"OK"}
```

- `success`/`message` are required.
- `payloadJson` is optional and echoed into ACK payload.
- `code` is optional; if present and `payloadJson` is omitted, host synthesizes a standard code payload.

## Latency tuning guidance

For slower external CLI invocations:

1. Keep host and bridge timeouts aligned:
   - Host `--adapter-request-timeout-ms 2500`
   - Bridge `--command-timeout-ms 2500`
2. Keep Core timeout >= host request timeout when running a real unlocker path:
   - Core `--unlocker-timeout-ms 1200` default is conservative; increase to `1500-2500` if commands are known to be slow.
3. Keep retries low for action commands to avoid accidental duplicate execution:
   - Core default `--unlocker-retry-count 0`.
