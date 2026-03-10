# Documentation Index

- [Architecture](/docs/architecture.md)
- [IPC Contract v1](/docs/ipc-contract.md)

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
