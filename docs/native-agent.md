# Native Agent

## Purpose

`UnlockerAgent.Native` is the x86 native foundation for the in-process unlocker backend used by `UnlockerAgentHost`.

Current milestone scope:
- bootstrap DLL exports
- command queue + heartbeat status plumbing
- PolyHook integration point wired (pinned vendor slot)

## Build

```powershell
./scripts/build-native-agent.ps1 -Configuration Release
```

Build prerequisites:
- CMake available on PATH
- Visual C++ x86 toolchain (MSVC Build Tools)

Build output:
- `artifacts/native-agent/build/<Configuration>/`

## PolyHook

Pinned source sync:

```powershell
./scripts/sync-polyhook.ps1
```

Pinned commit:
- `f4aee8e47383825469f924903357038b2efd8ca7`

Reference:
- [https://github.com/stevemk14ebr/PolyHook_2_0](https://github.com/stevemk14ebr/PolyHook_2_0)

## Evasion profile defaults

- Release: `full`
- Debug: `off`

Override via `UnlockerAgentHost`:

```powershell
dotnet run --project C:/Utilities/TalosForge/src/UnlockerAgentHost/TalosForge.UnlockerAgentHost.csproj -c Release -- --evasion-profile full
```

Runtime selection:
- `--runtime-mode auto|native|simulated`
- `auto`: uses native if DLL is found, otherwise falls back to simulated
- `native`: requires native DLL build and injection readiness
