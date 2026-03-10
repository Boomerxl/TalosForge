# TalosForge Core Architecture

## Runtime Pipeline

`MemoryReader -> ObjectManager -> Cache -> EventBus -> BotEngine -> UnlockerClient -> PluginHost`

## Components

- `MemoryReader`: external WoW process attach + typed reads.
- `ObjectManagerService`: scans object chain and returns immutable snapshots.
- `MemoryCacheService`: TTL policy cache (`Short=100ms`, `Long=15s`, `None=no-cache`).
- `EventBus`: diffs snapshots and emits typed events.
- `BotEngine`: adaptive scheduler (`Combat=35ms`, `Movement=70ms`, `Idle=120ms`, clamped `25-150ms`).
- `SharedMemoryUnlockerClient`: command/ack transport over ring buffers.
- `PluginHost`: in-process plugin execution with sandboxed command queue context.

## Public Contracts

- `IMemoryReader`
- `IObjectManager`
- `ICacheService`
- `IEventBus`
- `IUnlockerClient`
- `IPlugin` + `IPluginContext`
- `IBotEngine`

## Notes

- WoW target: 3.3.5a build 12340 (32-bit semantics).
- Unlocker remains external and command-driven.
- Navigation and movement are scaffolded for future MMap integration.
