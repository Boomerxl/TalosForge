# TalosForge Architecture

## Runtime process flow

`supervisor.ps1 -> dev-stack.ps1 -> (UnlockerAgentHost) + AdapterBridge + UnlockerHost + Core`

- `scripts/supervisor.ps1` is the thin entrypoint used by desktop UI and CLI operators.
- `scripts/dev-stack.ps1` starts/stops/tracks process state and run logs.
- `TalosForge.UI` calls supervisor `start|stop|status` and consumes status health JSON.

## Core execution pipeline

`MemoryReader -> ObjectManager -> EventBus/Cache -> BotEngine -> UnlockerClient -> PluginHost`

- `TalosForge.Core` reads world state and drives command emission.
- In-game hub rendering is read-only (Debug/Plugins tabs only) via Lua frame updates.
- Script/routine user command surfaces are disabled in Phase 1.

## Unlocker command path

`Core (SharedMemoryUnlockerClient) -> UnlockerHost (ring endpoint) -> AdapterBridge (pipe) -> AgentHost (wow-agent mode)`

- `TalosForge.UnlockerHost` reads commands from `TalosForge.Cmd.v1`, writes ACKs to `TalosForge.Evt.v1`.
- `TalosForge.AdapterBridge` validates/forwards adapter requests.
- `TalosForge.UnlockerAgentHost` manages native/simulated runtime and executes commands.

## Session trace context

All runtime processes support a shared `session_id` context:

- CLI option: `--session-id <value>`
- Env fallback: `TALOSFORGE_SESSION_ID`

Supervisor startup generates one session id for a stack run and propagates it to all child processes.

## Key files

- Supervisor: `scripts/supervisor.ps1`, `scripts/dev-stack.ps1`
- Desktop UI: `src/UI/TalosForge.UI/MainWindow.xaml`, `src/UI/TalosForge.UI/MainWindow.xaml.cs`
- Core runtime: `src/Core/Program.cs`, `src/Core/Runtime/BotRuntimeHost.cs`
- Host/Bridge/Agent startup: `src/UnlockerHost/Program.cs`, `src/AdapterBridge/Program.cs`, `src/UnlockerAgentHost/Program.cs`
