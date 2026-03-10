# Documentation Index

- [Architecture](/docs/architecture.md)
- [IPC Contract v1](/docs/ipc-contract.md)
- [Unlocker Host](/docs/unlocker-host.md)

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
