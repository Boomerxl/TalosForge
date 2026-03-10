# TalosForge Handoff (2026-03-10)

## 1) Mission and Scope
TalosForge is a modular .NET 8 WoW 3.3.5a framework with this core runtime pipeline:

`MemoryReader -> ObjectManager -> Cache -> EventBus -> BotEngine -> UnlockerClient -> PluginHost`

Current execution transport is shared-memory IPC (`TalosForge.Cmd.v1` / `TalosForge.Evt.v1`) with a standalone endpoint project `TalosForge.UnlockerHost`.

## 2) Current Plan Status

### Stage 1: Foundation hardening
Status: Completed.
- .NET 8 solution and project scaffolding.
- Contract interfaces in `src/Core/Abstractions`.
- Central options/logging setup.

### Stage 2: ObjectManager vertical core
Status: Completed (v1) and improved.
- Safe object traversal via object manager chain.
- Immutable snapshots (`WorldSnapshot`, `WowObjectSnapshot`, `PlayerSnapshot`).
- Binana-style explicit object layouts added in `src/Core/ObjectManager/ObjectLayouts.cs`.
- Local player getter + 200ms cache in `ObjectManagerService`.

### Stage 3: Cache + EventBus
Status: Completed (v1).
- TTL cache policies implemented.
- Snapshot diff eventing and typed events.

### Stage 4: Adaptive BotEngine
Status: Completed.
- Adaptive tick scheduling + watchdog + metrics.

### Stage 5: Shared-memory unlocker transport
Status: Completed + hardened.
- `SharedMemoryUnlockerClient` with correlation/timeout/retry.
- Timeout handling improved (no stack-trace spam).
- Standalone endpoint project: `src/UnlockerHost`.
- Host heartbeat status file + Core health monitor added.
- Client telemetry + adaptive backoff added.

### Stage 6: Plugin host v1
Status: Completed (v1) + desktop UX improvements.
- Isolated plugin host and sample plugin.
- Desktop UI control shell (`src/UI/TalosForge.UI`) with runtime controls.
- Unlocker state indicator now prominent (`Connected/Degraded/Disconnected`).

### Stage 7: Navigation/movement prep
Status: Kickoff done (interfaces/stubs).
- `INavigationService`, `IPathfinder`, `MmapNavigationStub`.
- Movement command primitives and policy placeholder.

## 3) What Works Right Now
- Attaches to local WoW process and runs stable bot ticks.
- Builds world snapshots and object scans without crashing loop.
- Plugin host loads and dispatches commands.
- Core and UnlockerHost run as separate processes with ACK flow.
- Unlocker health telemetry and heartbeat status are surfaced to runtime/UI.

## 4) Quick Runbook

### Build + tests
```powershell
dotnet build C:/Utilities/TalosForge/TalosForge.sln -c Release
dotnet test  C:/Utilities/TalosForge/TalosForge.sln -c Release
```

### Run UnlockerHost (mock executor)
```powershell
dotnet run --project C:/Utilities/TalosForge/src/UnlockerHost/TalosForge.UnlockerHost.csproj -c Release -- --executor mock
```

### Run Core against real endpoint mode
```powershell
dotnet run --project C:/Utilities/TalosForge/src/Core/TalosForge.Core.csproj -c Release -- --real-unlocker --ingame-ui --ingame-ui-interval 1
```

### Run desktop UI
```powershell
dotnet run --project C:/Utilities/TalosForge/src/UI/TalosForge.UI/TalosForge.UI.csproj -c Release
```

## 5) Key Files to Read First
1. `src/Core/Runtime/BotRuntimeHost.cs`
2. `src/Core/Bot/BotEngine.cs`
3. `src/Core/ObjectManager/ObjectManagerService.cs`
4. `src/Core/IPC/SharedMemoryUnlockerClient.cs`
5. `src/UnlockerHost/Host/UnlockerHostService.cs`
6. `src/UI/TalosForge.UI/MainForm.cs`
7. `docs/ipc-contract.md`, `docs/unlocker-host.md`, `docs/architecture.md`

## 6) Known Constraints / Notes
- Shared memory transport is Windows-only by design (`CA1416` warnings are expected).
- `UnlockerHost` currently uses `mock` / `null` executor modes only.
- No production command backend adapter yet; current endpoint validates contract and reliability.

## 7) Next Priorities (Recommended Order)
1. Implement `AdapterCommandExecutor` contract in `UnlockerHost` with strict payload validation and result codes.
2. Expand descriptor-backed reads in `ObjectManager` (health/combat/cast/auras) and map into `PlayerSnapshot`.
3. Add runtime/UI diagnostics panel for unlocker metrics (timeouts, backoff_ms, heartbeat age).
4. Improve movement opcodes semantics (`Stop` should get explicit opcode/contract, not overload).
5. Start navigation service real implementation behind existing interfaces.

## 8) Resource Pack
- Repository: https://github.com/Boomerxl/TalosForge
- Binana: https://github.com/thunderbrewhq/binana
- IceFlake reference: https://github.com/miceiken/IceFlake
- CallGraphCompare: https://github.com/gromchek/CallGraphCompare
- 3.3.5 offsets thread (cross-check): https://www.ownedcore.com/forums/world-of-warcraft/world-of-warcraft-bots-programs/wow-memory-editing/298310-3-3-5-offsets.html

Local Binana clone used during implementation:
- `C:/Utilities/_refs/binana`

## 9) Copy-Paste Prompt for Next Dev AI
```text
You are working in C:/Utilities/TalosForge on branch main.

Read first:
- docs/handoff-next-dev.md
- docs/architecture.md
- docs/ipc-contract.md
- docs/unlocker-host.md

Goal for this session:
Implement Stage 6.5 observability + Stage 7 kickoff improvements without breaking existing IPC and bot loop stability.

Required tasks:
1) Add Unlocker metrics panel in desktop UI:
   - show consecutive timeouts, total timeouts, backoff waits, last backoff ms, heartbeat age.
   - keep existing prominent Connected/Degraded/Disconnected badge.
2) Improve ObjectManager player mapping:
   - populate PlayerSnapshot with descriptor-backed Health/MaxHealth, InCombat, IsCasting when available.
   - preserve fail-safe behavior (no hard crash on unreadable fields).
3) Movement contract cleanup:
   - add explicit Stop opcode in Core contracts and host executor handling.
   - update UnlockerMovementController.StopAsync to use new opcode.
4) Add tests:
   - unit tests for new UI mapping helper logic (if extracted),
   - object manager descriptor mapping tests,
   - IPC/host test for new Stop opcode ACK.

Constraints:
- Keep .NET 8.
- No new external dependencies unless absolutely required.
- Preserve existing coding style and architecture.
- Ensure dotnet build + dotnet test pass.

Commit in small logical steps and push to main.
```
