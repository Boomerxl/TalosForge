# TalosForge Handoff (2026-03-11)

## 1) Mission and Scope
TalosForge is a modular .NET 8 WoW 3.3.5a framework.

Runtime pipeline:

`MemoryReader -> ObjectManager -> Cache -> EventBus -> BotEngine -> UnlockerClient -> PluginHost`

Primary transport between Core and external execution is shared-memory IPC:
- command ring: `TalosForge.Cmd.v1`
- ack ring: `TalosForge.Evt.v1`

Execution endpoint options include:
- `UnlockerHost --executor mock|null|adapter`
- `AdapterBridge --mode mock|process|wow-cli|wow-agent`
- `UnlockerAgentHost` persistent agent backend service

## 2) Current Baseline (Verified)
- Full solution build passes on Release.
- Full test suite passes (re-verify after pull/build).
- Shared-memory Core <-> UnlockerHost path is stable.
- Adapter path is active end-to-end:
  - host adapter backend (`pipe` / `unavailable`)
  - bridge modes (`mock`, `process`, `wow-cli`, `wow-agent`)
- `wow-agent` path now exists end-to-end via:
  - `src/UnlockerAgentHost` (persistent command host)
  - `AdapterBridge --mode wow-agent` (forwarder over named pipe)
  - `UnlockerAgentHost --runtime-mode auto|native|simulated`
- In-repo wow-cli wrapper now exists at `src/UnlockerCli/` and is auto-selected by `scripts/dev-stack.ps1` when `BridgeMode=wow-cli` and no explicit command path is provided.
- `wow-cli` opcode mappings are covered end-to-end in integration tests for:
  - `LuaDoString`, `CastSpellByName`, `SetTargetGuid`, `Face`, `MoveTo`, `Interact`, `Stop`
- Bridge/host error propagation now preserves structured payloads (`code` + `message` + diagnostics).
- Default timeout/retry/backoff settings are tuned for slower external command latency.
- Live smoke with WoW attached was run:
  - Core attach succeeded
  - in-game overlay command path active (`commands_count=1` ticks)
  - host adapter counters increased (`commands_read=3`, `acks_written=3`)
  - no repeated timeout warning spam during smoke

Verification run timestamp: **2026-03-11**.

## 3) What Was Completed This Session

### 3.1 Adapter/wow-cli hardening
- `WowCliBridgeCommandExecutor` now:
  - distinguishes timeout vs cancellation correctly
  - returns structured failure payloads for config/start/timeout/exit-code errors
- `ProcessBridgeCommandExecutor` now mirrors the same timeout/cancellation/error handling shape.
- Bridge service synthesizes structured payload JSON when an executor returns `code` without payload.

### 3.2 Timeout/retry/backoff defaults
- Core defaults:
  - `UnlockerTimeoutMs=1200`
  - `UnlockerRetryCount=0`
  - `UnlockerBackoffBaseMs=150`
  - `UnlockerBackoffMaxMs=2500`
- Host defaults:
  - `AdapterConnectTimeoutMs=1200`
  - `AdapterRequestTimeoutMs=2500`
- Bridge default:
  - `CommandTimeoutMs=2500`

### 3.3 Test coverage additions
- Added/updated tests for:
  - slow command response behavior (`wow-cli` executor)
  - timeout/recovery behavior (`NamedPipeAdapterBackend` timeout then success)
  - bridge error propagation format consistency through host ACK payload
  - end-to-end `wow-cli` opcode mapping through Core -> Host -> Bridge
  - in-game overlay publish path (`InGameOverlayService`) without requiring rendering validation

## 4) Build and Test Checklist (Do First)
1. `dotnet build C:/Utilities/TalosForge/TalosForge.sln -c Release`
2. `dotnet test C:/Utilities/TalosForge/TalosForge.sln -c Release`
3. If tests fail due stale artifacts or locked binaries, stop lingering `dotnet`/`TalosForge.AdapterBridge.exe` processes and rerun.

## 5) Runtime Smoke Runbook

### 5.1 Adapter smoke (mock bridge)
1. Bridge:
   - `dotnet run --project C:/Utilities/TalosForge/src/AdapterBridge/TalosForge.AdapterBridge.csproj -c Release -- --mode mock --pipe-name TalosForge.UnlockerAdapter.v1`
2. Host:
   - `dotnet run --project C:/Utilities/TalosForge/src/UnlockerHost/TalosForge.UnlockerHost.csproj -c Release -- --executor adapter --adapter-backend pipe --adapter-pipe TalosForge.UnlockerAdapter.v1`
3. Core:
   - `dotnet run --project C:/Utilities/TalosForge/src/Core/TalosForge.Core.csproj -c Release -- --real-unlocker --ingame-ui --ingame-ui-interval 1`
4. Confirm:
   - stable ticks
   - no repeated timeout warnings
   - host `commands_read` and `acks_written` increase
   - UI badge `Connected`

### 5.2 wow-cli smoke
1. Bridge:
   - `dotnet run --project C:/Utilities/TalosForge/src/AdapterBridge/TalosForge.AdapterBridge.csproj -c Release -- --mode wow-cli --pipe-name TalosForge.UnlockerAdapter.v1 --command-path C:/path/to/unlocker-cli.exe`
2. Host:
   - `dotnet run --project C:/Utilities/TalosForge/src/UnlockerHost/TalosForge.UnlockerHost.csproj -c Release -- --executor adapter --adapter-backend pipe --adapter-pipe TalosForge.UnlockerAdapter.v1`
3. Core:
   - `dotnet run --project C:/Utilities/TalosForge/src/Core/TalosForge.Core.csproj -c Release -- --real-unlocker --ingame-ui --ingame-ui-interval 1`
4. Validate opcode behavior in-game for all mapped opcodes and inspect ACK payload JSON on failures.

## 6) Key Files to Read First
1. `src/Core/Runtime/BotRuntimeHost.cs`
2. `src/Core/IPC/SharedMemoryUnlockerClient.cs`
3. `src/Core/Drawing/InGameOverlayService.cs`
4. `src/UnlockerHost/Program.cs`
5. `src/UnlockerHost/Execution/AdapterCommandExecutor.cs`
6. `src/UnlockerHost/Execution/NamedPipeAdapterBackend.cs`
7. `src/AdapterBridge/Program.cs`
8. `src/AdapterBridge/Execution/WowCliBridgeCommandExecutor.cs`
9. `src/AdapterBridge/Execution/WowCliCommandTranslator.cs`
10. `src/AdapterBridge/Execution/WowAgentBridgeCommandExecutor.cs`
11. `src/UnlockerAgentHost/Program.cs`
12. `src/UnlockerAgentHost/Execution/AgentCommandProcessor.cs`
13. `tests/TalosForge.Tests/IPC/UnlockerHostIntegrationTests.cs`

## 7) Known Constraints and Gaps
- Shared-memory transport is Windows-only by design (expected CA1416 warnings).
- Real in-game execution with native in-process hooks still needs final live validation for full behavior parity in production sessions.
- `wow-cli` remains available, but is fallback/debug due historical crash risk (`WoW ERROR #132`) from out-of-process remote-thread calls.
- Native agent foundation and PolyHook vendor slot are now in-repo; hooking/evasion internals require iterative hardening.
- If long-running smoke tests are interrupted, stale adapter/host processes can hold file locks and block rebuilds.

## 8) Recommended Next Priorities
1. Run `wow-agent` mode in live sessions and validate gameplay outcomes for every mapped opcode.
2. Complete native in-process execution binding (`UnlockerAgentHost` -> `UnlockerAgent.Native`) and tune hook readiness/timeout behavior.
3. Capture real-world latency distribution and tune `--agent-request-timeout-ms`, `--adapter-request-timeout-ms`, and `--unlocker-timeout-ms`.

## 9) External Research References (User-Supplied)

- IceFlake: [https://github.com/miceiken/IceFlake](https://github.com/miceiken/IceFlake)
- WoW binaries/call graph compare: [https://github.com/gromchek/CallGraphCompare](https://github.com/gromchek/CallGraphCompare)
- Binana (structs + function names): [https://github.com/thunderbrewhq/binana](https://github.com/thunderbrewhq/binana)
- PolyHook 2.0 (hooking framework): [https://github.com/stevemk14ebr/PolyHook_2_0](https://github.com/stevemk14ebr/PolyHook_2_0)
- WoW 3.3.5 offsets thread: [https://www.ownedcore.com/forums/world-of-warcraft/world-of-warcraft-bots-programs/wow-memory-editing/298310-3-3-5-offsets.html](https://www.ownedcore.com/forums/world-of-warcraft/world-of-warcraft-bots-programs/wow-memory-editing/298310-3-3-5-offsets.html)
- Basic memory driver example: [https://github.com/ryan-weil/ReadWriteDriver](https://github.com/ryan-weil/ReadWriteDriver)
- Friend project reference (local): `C:/Utilities/VectorBuddy`

Note:
- `C:/Utilities/VectorBuddy` is DLL/plugin based (`HWBPEngine.dll`) rather than a standalone TalosForge-compatible CLI. It runs in-process, patches Lua pointer checks at startup, and queues Lua on the game-thread render path.
- `IceFlake` is also an in-process injected framework model, which aligns with the safer direction for live command execution.
- TalosForge now includes a wrapper CLI scaffold at `src/UnlockerCli/` and `scripts/dev-stack.ps1` can auto-use it for `BridgeMode=wow-cli`.
