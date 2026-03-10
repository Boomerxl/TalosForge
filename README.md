# TalosForge

TalosForge is a modular WoW 3.3.5a automation framework for private-server research workflows.

## Current runtime
- .NET 8 console solution (`TalosForge.sln`)
- External memory access via Kernel32 P/Invoke (`OpenProcess`, `ReadProcessMemory`, `CloseHandle`)
- WoW attach baseline with 32-bit process guard
- Core contracts for ObjectManager, cache, event bus, bot engine, unlocker IPC, and plugins

## Planned systems
- Object manager world snapshots
- TTL cache and state-diff events
- Adaptive bot loop
- Shared-memory unlocker transport
- In-process plugin host and sample routine
- Navigation/movement integration hooks

**Disclaimer**: educational/private-server use only.
