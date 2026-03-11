# Unlocker CLI Integration Onboarding

## Purpose

TalosForge `AdapterBridge --mode wow-cli` requires a real external CLI executable that can execute these verbs against WoW:

- `lua <code>`
- `cast <spell>`
- `target <guid>`
- `face <facing> <smoothing>`
- `moveto <x> <y> <z> <overshootThreshold>`
- `interact [guid]`
- `stop`

Current repo status:
- Adapter/Host/Core pipeline is implemented and tested.
- A new in-repo CLI wrapper now exists:
  - `src/UnlockerCli/TalosForge.UnlockerCli.csproj`
  - default verb behavior executes Lua remotely via WoW process memory (using known 3.3.5a addresses from VectorBuddy context).
- `wow-agent` mode now exists and is the preferred real-session path for persistent in-process architecture.

## Current Candidate: VectorBuddy

Local project path:
- `C:/Utilities/VectorBuddy`

Current state:
- `VectorBuddy` currently provides DLL + plugin/injection workflow (`HWBPEngine.dll`), not a standalone command-line executable with the TalosForge verb contract above.
- It can still be used as a base for a wrapper CLI implementation.

## Source verification snapshot (2026-03-11)

Checked sources and key findings:

- `IceFlake` is an injected WoW 3.3.5a memory framework (C#/.NET), not a standalone external command bridge.
- `VectorBuddy` startup path is in-process DLL execution:
  - installs hooks/VEH/HWBP from `dllmain.cpp` startup thread
  - patches Lua pointer validation path (`LuaEngine::PatchInvalidPtrCheckRange()`)
  - executes queued Lua on game-thread render loop (`hkPresent`/overlay flow in project docs)
- This differs from TalosForge in-repo CLI's current out-of-process remote-thread call model and explains why direct `wow-cli` shellcode invocation can be unstable in live WoW sessions.

Practical implication:
- For reliable in-game execution readiness, prefer an injected in-process backend (plugin/DLL bridge) over direct external `CreateRemoteThread` Lua calls.
- Keep `wow-cli` for fallback/debug only.

## Required CLI Contract for TalosForge

For each invocation:
1. Process receives verb + args.
2. Executes requested action in-game.
3. Returns exit code:
   - `0` success
   - non-zero failure
4. Writes one first stdout line for human-readable ACK/failure detail.

`AdapterBridge` behavior:
- exit `0` => success ACK
- non-zero => failure ACK (`BRIDGE_WOWCLI_EXIT_CODE` with structured diagnostics payload)

## In-Repo CLI (TalosForge.UnlockerCli)

Supported verbs:
- `lua <code>`
- `cast <spell>`
- `target <guid>`
- `face <facing> <smoothing>`
- `moveto <x> <y> <z> <overshootThreshold>`
- `interact [guid]`
- `stop`

Defaults:
- WoW process name: `Wow`
- Lua execute address: `0x00819210`
- Hardware event flag address: `0x00B499A4`

Override example:

```powershell
TalosForge.UnlockerCli.exe lua "print('hi')" --lua-exec-addr 0x00819210 --hardware-flag-addr 0x00B499A4
```

## Suggested Implementation Plan

1. Build or wrap a CLI executable (`unlocker-cli.exe`) that accepts the TalosForge verbs.
2. If wrapping `VectorBuddy`, add a command surface around its Lua/cast capabilities.
3. Verify each opcode manually using:
   - `LuaDoString`
   - `CastSpellByName`
   - `SetTargetGuid`
   - `Face`
   - `MoveTo`
   - `Interact`
   - `Stop`
4. Run full TalosForge stack in `wow-cli` mode and validate visible in-game effects.

## Dev Stack Setup (once per shell)

Set CLI path and optional args:

```powershell
$env:TALOSFORGE_UNLOCKER_CLI_PATH = "C:/path/to/unlocker-cli.exe"
$env:TALOSFORGE_UNLOCKER_CLI_ARGS = "--optional --args"
```

Start stack:

```powershell
./scripts/dev-stack.ps1 -Action start -BridgeMode wow-cli -UseRealUnlocker:$true -EnableInGameUi:$true -InGameUiInterval 1
```

Note:
- `scripts/dev-stack.ps1` auto-builds and uses the in-repo `TalosForge.UnlockerCli.exe` for `BridgeMode=wow-cli` when no explicit `-BridgeCommandPath` is set.

Stop stack:

```powershell
./scripts/dev-stack.ps1 -Action stop
```

## User-Provided Research Resources

- IceFlake: [https://github.com/miceiken/IceFlake](https://github.com/miceiken/IceFlake)
- WoW binaries / call graph compare: [https://github.com/gromchek/CallGraphCompare](https://github.com/gromchek/CallGraphCompare)
- Binana (structs/function names): [https://github.com/thunderbrewhq/binana](https://github.com/thunderbrewhq/binana)
- PolyHook 2.0 (hooking library): [https://github.com/stevemk14ebr/PolyHook_2_0](https://github.com/stevemk14ebr/PolyHook_2_0)
- WoW 3.3.5 offsets thread: [https://www.ownedcore.com/forums/world-of-warcraft/world-of-warcraft-bots-programs/wow-memory-editing/298310-3-3-5-offsets.html](https://www.ownedcore.com/forums/world-of-warcraft/world-of-warcraft-bots-programs/wow-memory-editing/298310-3-3-5-offsets.html)
- Basic driver example: [https://github.com/ryan-weil/ReadWriteDriver](https://github.com/ryan-weil/ReadWriteDriver)
- Friend project reference (local): `C:/Utilities/VectorBuddy`
