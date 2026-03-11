# Adapter Bridge

## Purpose

`TalosForge.AdapterBridge` is the companion process for `UnlockerHost --executor adapter --adapter-backend pipe`.

It accepts adapter protocol requests over a named pipe and returns adapter protocol responses.

Default pipe name: `TalosForge.UnlockerAdapter.v1`
Default agent pipe for `wow-agent`: `TalosForge.Agent.v1`

## Run

Mock mode (echo ACKs, good for end-to-end validation):

```powershell
dotnet run --project C:/Utilities/TalosForge/src/AdapterBridge/TalosForge.AdapterBridge.csproj -c Release -- --mode mock --pipe-name TalosForge.UnlockerAdapter.v1
```

Process mode (generic request/response JSON bridge command):

```powershell
dotnet run --project C:/Utilities/TalosForge/src/AdapterBridge/TalosForge.AdapterBridge.csproj -c Release -- --mode process --pipe-name TalosForge.UnlockerAdapter.v1 --command-path C:/path/to/bridge.exe --command-args "--some --args"
```

WoW CLI mode (direct opcode-to-command execution):

```powershell
dotnet run --project C:/Utilities/TalosForge/src/AdapterBridge/TalosForge.AdapterBridge.csproj -c Release -- --mode wow-cli --pipe-name TalosForge.UnlockerAdapter.v1 --command-path C:/Utilities/TalosForge/src/UnlockerCli/bin/Release/net8.0/TalosForge.UnlockerCli.exe
```

WoW Agent mode (persistent in-process agent backend):

```powershell
dotnet run --project C:/Utilities/TalosForge/src/UnlockerAgentHost/TalosForge.UnlockerAgentHost.csproj -c Release -- --pipe-name TalosForge.Agent.v1 --evasion-profile full
dotnet run --project C:/Utilities/TalosForge/src/AdapterBridge/TalosForge.AdapterBridge.csproj -c Release -- --mode wow-agent --pipe-name TalosForge.UnlockerAdapter.v1 --agent-pipe TalosForge.Agent.v1 --agent-connect-timeout-ms 1200 --agent-request-timeout-ms 2500 --agent-evasion-profile full
```

Safety note:
- `wow-agent` is the recommended real-session path.
- `wow-cli` is retained as fallback/debug and is considered experimental for live clients.

## CLI options

- `--pipe-name`
- `--mode mock|process|wow-cli|wow-agent`
- `--command-path`
- `--command-args`
- `--command-timeout-ms` (default `2500`)
- `--agent-pipe` (default `TalosForge.Agent.v1`)
- `--agent-connect-timeout-ms` (default `1200`)
- `--agent-request-timeout-ms` (default `2500`)
- `--agent-evasion-profile off|standard|full`
- `--smoke`
- `--smoke-seconds`

## Protocol

Request (one JSON line):

```json
{"version":1,"commandId":123,"opcode":"LuaDoString","opcodeValue":1,"payloadJson":"{\"code\":\"print('hi')\"}","timestampUnixMs":1730842800000}
```

Response (one JSON line):

```json
{"success":true,"message":"ACK:LuaDoString","payloadJson":"{\"bridge\":true}","code":"OK"}
```

Notes:
- `message` should always be populated.
- `payloadJson` is optional and is passed back through host ACK payload.
- `code` is optional and used for structured diagnostics.
- if an executor returns `code` but no `payloadJson`, bridge synthesizes:
  - `{"code":"<code>","message":"<message>"}`

## wow-cli opcode mapping

`wow-cli` maps validated opcodes to executable arguments:

- `LuaDoString` -> `lua <code>`
- `CastSpellByName` -> `cast <spell>`
- `SetTargetGuid` -> `target <guid>`
- `Face` -> `face <facing> <smoothing>`
- `MoveTo` -> `moveto <x> <y> <z> <overshootThreshold>`
- `Interact` -> `interact [guid]`
- `Stop` -> `stop`

CLI command contract for `wow-cli`:
- exit code `0` => success ACK
- non-zero exit code => failure ACK
- first stdout line becomes ACK message (fallback is `ACK:<Opcode>`)
- failures return structured `code` and `payloadJson` (including diagnostics such as timeout/exit code)

TalosForge in-repo CLI wrapper:
- project: `src/UnlockerCli/TalosForge.UnlockerCli.csproj`
- defaults:
  - WoW process: `Wow`
  - Lua execute address: `0x00819210`
  - hardware event flag address: `0x00B499A4`

Runtime tuning tip:
- Core defaults are tuned for slower external CLI latency:
  - `UnlockerTimeoutMs=1200`
  - `UnlockerRetryCount=0`
  - `UnlockerBackoffBaseMs=150`
  - `UnlockerBackoffMaxMs=2500`
- Host adapter defaults:
  - `AdapterConnectTimeoutMs=1200`
  - `AdapterRequestTimeoutMs=2500`
- Bridge `--command-timeout-ms` default is `2500`.
- Bridge `--agent-connect-timeout-ms` default is `1200`.
- Bridge `--agent-request-timeout-ms` default is `2500`.

## Real wow-agent validation runbook

1. Start UnlockerAgentHost:
   - `dotnet run --project C:/Utilities/TalosForge/src/UnlockerAgentHost/TalosForge.UnlockerAgentHost.csproj -c Release -- --pipe-name TalosForge.Agent.v1 --evasion-profile full`
2. Start AdapterBridge in wow-agent mode:
   - `dotnet run --project C:/Utilities/TalosForge/src/AdapterBridge/TalosForge.AdapterBridge.csproj -c Release -- --mode wow-agent --pipe-name TalosForge.UnlockerAdapter.v1 --agent-pipe TalosForge.Agent.v1`
3. Start UnlockerHost adapter backend:
   - `dotnet run --project C:/Utilities/TalosForge/src/UnlockerHost/TalosForge.UnlockerHost.csproj -c Release -- --executor adapter --adapter-backend pipe --adapter-pipe TalosForge.UnlockerAdapter.v1`
4. Start Core:
   - `dotnet run --project C:/Utilities/TalosForge/src/Core/TalosForge.Core.csproj -c Release -- --real-unlocker --ingame-ui --ingame-ui-interval 1`
5. Verify end-to-end opcode coverage in-game:
   - `LuaDoString`
   - `CastSpellByName`
   - `SetTargetGuid`
   - `Face`
   - `MoveTo`
   - `Interact`
   - `Stop`
6. On failures, confirm ACK payload includes structured `code` + `message` + diagnostics JSON.

## Real wow-cli fallback runbook

1. Start AdapterBridge in wow-cli mode with your unlocker CLI binary:
   - `dotnet run --project C:/Utilities/TalosForge/src/AdapterBridge/TalosForge.AdapterBridge.csproj -c Release -- --mode wow-cli --pipe-name TalosForge.UnlockerAdapter.v1 --command-path C:/Utilities/TalosForge/src/UnlockerCli/bin/Release/net8.0/TalosForge.UnlockerCli.exe`
2. Start UnlockerHost adapter backend:
   - `dotnet run --project C:/Utilities/TalosForge/src/UnlockerHost/TalosForge.UnlockerHost.csproj -c Release -- --executor adapter --adapter-backend pipe --adapter-pipe TalosForge.UnlockerAdapter.v1`
3. Start Core:
   - `dotnet run --project C:/Utilities/TalosForge/src/Core/TalosForge.Core.csproj -c Release -- --real-unlocker --ingame-ui --ingame-ui-interval 1`
4. Verify end-to-end opcode coverage in-game:
   - `LuaDoString`
   - `CastSpellByName`
   - `SetTargetGuid`
   - `Face`
   - `MoveTo`
   - `Interact`
   - `Stop`
5. On failures, confirm ACK payload includes structured `code` + `message` JSON.
