# TalosForge

TalosForge is a WoW 3.3.5a automation framework with a multi-process runtime:

- `TalosForge.Core` (bot/runtime loop)
- `TalosForge.UnlockerHost` (shared-memory command/ack endpoint)
- `TalosForge.AdapterBridge` (adapter transport bridge)
- `TalosForge.UnlockerAgentHost` (native/simulated in-process agent runtime)
- `TalosForge.UI` (desktop operator UI wired to supervisor start/stop/status)

## Supervisor entrypoint

Use the thin supervisor script contract:

- `scripts/supervisor.ps1 -Action start`
- `scripts/supervisor.ps1 -Action stop`
- `scripts/supervisor.ps1 -Action status -Json`

The supervisor delegates process orchestration to `scripts/dev-stack.ps1` and reports health JSON for status polling.

## Session trace context

Core/Host/Bridge/Agent support optional session metadata:

- CLI: `--session-id <value>` (all services)
- Env: `TALOSFORGE_SESSION_ID` fallback when CLI arg is omitted

When started through the supervisor, one shared session id is generated and reused across all runtime processes.

## Testing

- Deterministic tests and live smoke tests are split in CI.
- Live smoke tests use explicit xUnit skip semantics when runtime preconditions are unmet (for example, WoW not running or agent not injected).

## Documentation

See [docs/README.md](/docs/README.md) for architecture and IPC details.
