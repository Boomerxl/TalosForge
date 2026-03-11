# TalosForge Handoff (2026-03-11)

## 1) Current Situation
TalosForge remains on the `wow-agent` migration path, but native runtime tooling is now unblocked on this machine.

Status now:
- Native toolchain is installed and usable.
- `UnlockerAgent.Native` builds successfully.
- 30-minute `wow-agent` soak was executed in `native` runtime mode with stable counters and no crash signatures.
- `wow-cli` stays fallback/debug only due historical WoW crash risk (`ERROR #132`) on remote-thread path.

## 2) Verified in This Session
- Baseline verification:
  - `dotnet build C:/Utilities/TalosForge/TalosForge.sln -c Release` passed.
  - `dotnet test C:/Utilities/TalosForge/TalosForge.sln -c Release` failed once on known timeout flake, then passed on single rerun (**75/75**).
- Native prerequisites:
  - CMake installed (`Kitware.CMake 4.2.3`).
  - VS Build Tools 2022 + x86 MSVC tooling available.
  - Windows SDK libs are present (`C:/Program Files (x86)/Windows Kits/10/Lib/10.0.19041.0/...`).
- Native build:
  - `./scripts/build-native-agent.ps1 -Configuration Release` passed.
  - Output DLL: `C:/Utilities/TalosForge/artifacts/native-agent/build/Release/Release/TalosForge.UnlockerAgent.Native.dll`.
- Native soak:
  - `./scripts/soak-wow-agent.ps1 -DurationMinutes 30 -AgentRuntimeMode native` passed.
  - Run directory: `C:/Utilities/TalosForge/artifacts/dev-stack/run-20260311-172120`.

## 3) Soak Evidence
- Agent runtime:
  - `Agent runtime mode=native.`
  - No `fallback to simulated` entries.
- Host throughput:
  - First sampled stats: `commands_read=36 acks_written=36 acks_dropped=0 decode_failures=0 executor_failures=0`
  - Final sampled stats: `commands_read=14207 acks_written=14207 acks_dropped=0 decode_failures=0 executor_failures=0`
- Timeout/crash indicators:
  - `ERROR #132`: 0 matches in run logs.
  - `BRIDGE_WOWAGENT_TIMEOUT`: 0 matches.
  - all `*.err.log` files were empty.
- Core behavior:
  - Stable bot ticks and repeated successful snapshots through end of run.

## 4) Code Changes Made
`scripts/build-native-agent.ps1` was hardened:
- resolves `cmake` from PATH or default install path (`C:/Program Files/CMake/bin/cmake.exe`);
- validates configure/build `cmake` exit codes and fails fast;
- clears stale CMake cache when generator changes;
- keeps VS generator path and adds fallback logic for environments where VS instance registration is missing.

These changes are small and local to native build bootstrap; IPC/runtime contracts were not changed.

## 5) Remaining Validation Gap
The only non-automated check is explicit human confirmation of in-game UI visual stability during the soak window.  
Logs indicate stable runtime behavior, but UI stability still requires observer confirmation in-session.

## 6) Operational Notes
- Keep .NET 8.
- Keep IPC contracts unchanged.
- Keep `wow-cli` as fallback/debug only.
- If `dotnet test` hits the known timing flake, rerun once before code changes.

## 7) Suggested Next Steps
1. Perform/record explicit manual UI stability observation during a native soak window.
2. If desired, add one lightweight runtime log line when native runtime transitions to ready state (for stronger non-UI proof of injection readiness).
