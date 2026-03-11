# Documentation Index

- [Architecture](/docs/architecture.md)
- [IPC Contract v1](/docs/ipc-contract.md)
- [Unlocker Host](/docs/unlocker-host.md)
- [Adapter Bridge](/docs/adapter-bridge.md)
- [Native Agent](/docs/native-agent.md)
- [Unlocker CLI Integration](/docs/unlocker-cli-integration.md)
- [Next Dev Handoff](/docs/handoff-next-dev.md)

## Automation

Documentation checks run in CI via `.github/workflows/ci.yml` using `scripts/verify-docs.ps1`.
API XML docs are generated from C# comments during build (`GenerateDocumentationFile=true`).

## Local cleanup

Use `scripts/clean.ps1` to remove build artifacts (`bin/`, `obj/`, `TestResults/`) after local runs.

## Runtime telemetry CLI

- `--telemetry-interval N`: snapshot telemetry every `N` ticks (`N <= 0` disables snapshot telemetry).
- `--telemetry-level minimal|normal|debug`:
  - `minimal`: tick metrics only
  - `normal`: tick metrics + periodic snapshot summary
  - `debug`: normal + object preview and detailed snapshot failure reason
- `--plugin-dir PATH`: override plugin discovery path.
- `--ingame-ui`: enable lightweight in-game status overlay (via unlocker Lua command).
- `--ingame-ui-interval N`: overlay update every `N` ticks.
- `--real-unlocker`: disable mock endpoint and use external unlocker IPC.
- `--use-mock-unlocker`: force mock endpoint (default for console runs).
- `--unlocker-timeout-ms N`: ACK wait timeout for unlocker commands.
- `--unlocker-retry-count N`: retry attempts after a timeout/write miss.
- `--unlocker-backoff-base-ms N`: adaptive timeout backoff base delay.
- `--unlocker-backoff-max-ms N`: adaptive timeout backoff max delay.

Default plugin discovery order:
1. `<app>/plugins` when manifests exist
2. `src/Plugins/SampleCombatPlugin/bin/Debug/net8.0` when available
3. `src/Plugins/SampleCombatPlugin/bin/Release/net8.0` when available
4. fallback/create `<app>/plugins`

## Desktop UI (6a)

- Project: `src/UI/TalosForge.UI`
- Run:
  - `dotnet run --project C:/Utilities/TalosForge/src/UI/TalosForge.UI/TalosForge.UI.csproj`
- Features:
  - Start/Stop runtime loop
  - Telemetry level + interval controls
  - In-game UI toggle + interval controls
  - Mock/real unlocker mode toggle
  - Optional plugin directory override
  - Live metrics panel (status, tick, objects, target, commands)
  - Unlocker health indicator (Connected/Degraded/Disconnected)
  - Live runtime log output

In-game overlay visibility requires:
1. `In-game UI` enabled
2. `Use Mock Unlocker` disabled
3. external unlocker service actively consuming `TalosForge.Cmd.v1` and writing ACKs to `TalosForge.Evt.v1`

## UnlockerHost service

- Project: `src/UnlockerHost/TalosForge.UnlockerHost.csproj`
- Run (mock executor):
  - `dotnet run --project C:/Utilities/TalosForge/src/UnlockerHost/TalosForge.UnlockerHost.csproj -c Release -- --executor mock`
- Run Core against host:
  - `dotnet run --project C:/Utilities/TalosForge/src/Core/TalosForge.Core.csproj -c Release -- --real-unlocker`
- Host heartbeat file:
  - `%TEMP%/TalosForge.UnlockerHost.status.json` (default)

## AdapterBridge service

- Project: `src/AdapterBridge/TalosForge.AdapterBridge.csproj`
- Run (mock bridge):
  - `dotnet run --project C:/Utilities/TalosForge/src/AdapterBridge/TalosForge.AdapterBridge.csproj -c Release -- --mode mock`
- Run (wow-cli bridge):
  - `dotnet run --project C:/Utilities/TalosForge/src/AdapterBridge/TalosForge.AdapterBridge.csproj -c Release -- --mode wow-cli --command-path C:/path/to/unlocker-cli.exe`
- Run (wow-agent bridge):
  - `dotnet run --project C:/Utilities/TalosForge/src/AdapterBridge/TalosForge.AdapterBridge.csproj -c Release -- --mode wow-agent --agent-pipe TalosForge.Agent.v1`
- Used by UnlockerHost adapter mode:
  - `dotnet run --project C:/Utilities/TalosForge/src/UnlockerHost/TalosForge.UnlockerHost.csproj -c Release -- --executor adapter --adapter-backend pipe`

## UnlockerAgentHost service

- Project: `src/UnlockerAgentHost/TalosForge.UnlockerAgentHost.csproj`
- Run:
  - `dotnet run --project C:/Utilities/TalosForge/src/UnlockerAgentHost/TalosForge.UnlockerAgentHost.csproj -c Release -- --pipe-name TalosForge.Agent.v1 --evasion-profile full`
- Notes:
  - persistent process that manages agent session lifecycle
  - executes commands through a safe queue model
  - defaults: `full` evasion profile in Release, `off` in Debug (can be overridden)

## One-command local stack

Use `scripts/dev-stack.ps1` to start/stop/status the full bridge+host+core stack from one terminal.

- Start (default: bridge `mock`, host `adapter+pipe`, core `--real-unlocker --ingame-ui --ingame-ui-interval 1`):
  - `./scripts/dev-stack.ps1 -Action start`
- Stop all three:
  - `./scripts/dev-stack.ps1 -Action stop`
- Check status + log paths:
  - `./scripts/dev-stack.ps1 -Action status`

Optional `wow-cli` startup example:

- `./scripts/dev-stack.ps1 -Action start -BridgeMode wow-cli -BridgeCommandPath C:/path/to/unlocker-cli.exe`
- Or set once per shell:
  - `$env:TALOSFORGE_UNLOCKER_CLI_PATH = "C:/path/to/unlocker-cli.exe"`
  - `$env:TALOSFORGE_UNLOCKER_CLI_ARGS = "--optional --args"`
  - `./scripts/dev-stack.ps1 -Action start -BridgeMode wow-cli`
- Or use in-repo CLI wrapper (auto-selected when path is not provided):
- `./scripts/dev-stack.ps1 -Action start -BridgeMode wow-cli`

Recommended real-session startup (`wow-agent`):

- `./scripts/dev-stack.ps1 -Action start -BridgeMode wow-agent`
- Force native runtime (no simulated fallback):
  - `./scripts/dev-stack.ps1 -Action start -BridgeMode wow-agent -AgentRuntimeMode native`

Soak script (30+ min):

- `./scripts/soak-wow-agent.ps1 -DurationMinutes 30 -AgentRuntimeMode native`

Important:
- `BridgeMode mock` only returns ACKs and does not execute in-game Lua/actions. Use `BridgeMode wow-cli` with a real unlocker CLI binary for visible in-game overlay.
- `BridgeMode wow-agent` is the preferred path for stable in-process execution architecture.
- In-repo wrapper CLI is at `src/UnlockerCli/TalosForge.UnlockerCli.csproj` and uses WoW 3.3.5a Lua execution addresses by default.

Logs and PID state are written under:
- `artifacts/dev-stack/`
