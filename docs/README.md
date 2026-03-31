# Documentation Index

- [Architecture](/docs/architecture.md)
- [IPC Contract](/docs/ipc-contract.md)
- [Unlocker Host](/docs/unlocker-host.md)
- [Adapter Bridge](/docs/adapter-bridge.md)
- [Native Agent](/docs/native-agent.md)
- [Unlocker CLI Integration](/docs/unlocker-cli-integration.md)

## Supervisor workflow

- Thin entrypoint: `scripts/supervisor.ps1`
- Actions:
  - `start`
  - `stop`
  - `status -Json` (health JSON output)
- Backend orchestrator: `scripts/dev-stack.ps1`

Desktop UI (`src/UI/TalosForge.UI`) calls this supervisor contract instead of launching/injecting components directly.

## CI checks

CI workflow `.github/workflows/ci.yml` runs:

1. build
2. deterministic tests (`Suite!=LiveSmoke`)
3. live smoke tests (`Suite=LiveSmoke`)
4. docs verification (`scripts/verify-docs.ps1`)
