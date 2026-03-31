# TalosForge In-Game ImGui GUI — Handoff Prompt

> **Status (2026):** The injected agent no longer uses Dear ImGui or a D3D9 hook. In-game UI is **WoW Lua frames** only: `src/UnlockerAgent.Native/src/LuaFrameOverlay.cpp` mirrors `TalosForge.Core.Debug.DebugFrameLua.CreateFrame()`. The sections below describe the **former** ImGui plan for historical reference or if you reintroduce GPU overlays.
>
> **For new work:** use **`docs/lua-addon-style-gui-handoff.md`** — addon-style Frame API, debug/plugins/scripts/routines, repo map, and a copy-paste agent prompt.

## Goal (historical)

Implement a Dear ImGui overlay rendered inside WoW 3.3.5a (32-bit DirectX 9) for the TalosForge bot framework. The overlay provides debug visualization and bot control panels. It must coexist with TalosForge's existing anti-detection architecture (PEB hiding, PE header erasure, SetTimer dispatch).

---

## Project Location

`c:\Utilities\TalosForge`

---

## What Already Exists

### Native Agent (`d3dhelper.dll`, 32-bit C++20)

- **Source**: `src/UnlockerAgent.Native/src/UnlockerAgentExports.cpp` (~1400 lines)
- **Headers**: `src/UnlockerAgent.Native/include/*.h`
- **Build**: `scripts/build-native-agent.ps1` runs CMake → MSVC → outputs `artifacts/native-agent/build/Release/Release/d3dhelper.dll`
- **CMakeLists.txt**: `src/UnlockerAgent.Native/CMakeLists.txt` (static CRT, C++20, /EHa for SEH)
- The DLL is injected into WoW.exe (32-bit) via `CreateRemoteThread` + `LoadLibraryW`

### Current Architecture

1. **Anti-detection**: `DllMain(DLL_PROCESS_ATTACH)` hides from PEB, clears debug flags, erases PE header
2. **Optional deferred activation**: `kDeferredActivationMs` in `UnlockerAgentExports.cpp` (currently **0**) — when 0, the startup thread installs the `SetTimer` callback as soon as the game window is ready (no sleep)
3. **Game-thread dispatch**: `DispatchTimerProc` (via `WM_TIMER`) processes a queue of Lua commands. All Lua calls happen on the game thread via `luaL_loadbuffer` + `lua_pcall`
4. **IPC**: Named pipe server on a background thread receives opcodes from the .NET host, enqueues Lua for game-thread execution, returns results
5. **Lua query support**: `DispatchLuaQueryOnGameThread` captures return values via `lua_tolstring` for opcodes like `QuerySpellInfo`, `QueryBags`, `QueryAuras`

### Key Globals (in anonymous namespace inside UnlockerAgentExports.cpp)

- `g_gameWindow` — HWND of WoW's main window
- `g_timerInstalled` — whether the SetTimer dispatch is active
- `g_module` — HMODULE of the injected DLL
- `g_state` — atomic AgentState (Booting/Ready/Faulted)

### Data Available to Display

The .NET host has rich game state, but for the native ImGui overlay, you'll want to either:
- (A) Read WoW memory directly from the native agent (addresses are known, see Offsets.cs)
- (B) Receive state from the .NET host via named pipe as a periodic JSON snapshot

**Option A is recommended** for a responsive overlay since you're already in-process.

Key models from the managed side (replicate the reads natively):

```
WowObjectSnapshot: Guid, Type, Position(X/Y/Z), Facing, Health, MaxHealth, Mana, MaxMana, Level, EntryId, IsDead, UnitFlags, DynamicFlags, Name, FactionTemplate, Auras[]
PlayerSnapshot: Guid, Position, Facing, TargetGuid, InCombat, IsCasting, Health, MaxHealth, Mana, MaxMana, Level, Name, Auras[]
AuraInfo: SpellId, CasterGuid, Flags, Stacks, DurationMs, EndTimeMs
```

Key offsets (from `src/Core/Offsets.cs`):

```
STATIC_CLIENT_CONNECTION = 0x00C79CE0
OBJECT_MANAGER_OFFSET    = 0x2ED0
FIRST_OBJECT_OFFSET      = 0x00AC
NEXT_OBJECT_OFFSET       = 0x003C
OBJECT_GUID              = 0x0030
OBJECT_TYPE              = 0x0014
OBJECT_DESCRIPTOR_PTR    = 0x0008
OBJECT_POS_X/Y/Z         = 0x079C / 0x0798 / 0x07A0
OBJECT_ROTATION          = 0x07A8
LOCAL_GUID_OFFSET         = 0x00C0
LOCAL_TARGET_GUID_STATIC  = 0x00BD07B0

CGUnit offsets (from object base):
  Health=0x1068, MaxHealth=0x1088, Mana=0x106C, MaxMana=0x108C
  CombatFlag=0x0BEC, SpellCastStart=0x0A78, SpellCastEnd=0x0A7C

Descriptor fields (from descriptor ptr at object+0x08):
  DESC_OBJECT_ENTRY       = 0x000C
  DESC_UNIT_LEVEL         = 0x00D8
  DESC_UNIT_FLAGS         = 0x00EC
  DESC_UNIT_DYNAMIC_FLAGS = 0x013C

Creature name: *(*(object + 0x0964) + 0x005C) -> null-terminated string
```

### Existing Lua Debug Frame (Phase 2a — already done)

`src/Core/Debug/DebugOverlayService.cs` and `src/Core/Debug/DebugFrameLua.cs` implement a simple Lua-based text overlay using WoW's native frame API. This is the low-risk fallback. The ImGui overlay is the rich alternative.

---

## What to Build

### 1. Vendor Dear ImGui into the native agent

- Download ImGui v1.92.x source files (`imgui.cpp`, `imgui.h`, `imgui_draw.cpp`, `imgui_tables.cpp`, `imgui_widgets.cpp`, `imgui_internal.h`, `imstb_*.h`, `imconfig.h`)
- Also get `imgui_impl_dx9.cpp/h` and `imgui_impl_win32.cpp/h` from `backends/`
- Place in `src/UnlockerAgent.Native/third_party/imgui/`
- Add all imgui `.cpp` files to `CMakeLists.txt`
- Link `d3d9.lib` (system library)

### 2. D3D9 EndScene Hook for Rendering

**Strategy: vtable hook with careful restoration**

WoW 3.3.5a uses Direct3D 9. To render ImGui, hook `IDirect3DDevice9::EndScene` (vtable index 42) and `Reset` (vtable index 16).

Approach:
1. Create a temporary D3D9 device to get the vtable pointer
2. Save the original `EndScene` and `Reset` function pointers
3. Replace vtable entries with your hooks
4. In the hooked `EndScene`: initialize ImGui on first call, then render the overlay
5. In the hooked `Reset`: release ImGui resources, re-create after reset
6. On shutdown: restore original vtable entries

**IMPORTANT STEALTH CONSIDERATIONS:**
- The previous EndScene hook approach was detected by Warden. The SetTimer dispatch replaced it.
- For ImGui rendering ONLY, the vtable hook is acceptable because:
  - It only adds rendering calls, doesn't modify game state
  - If `kDeferredActivationMs` is non-zero, install the hook after that period; with 0, the agent activates immediately
  - Consider restoring the vtable between frames if Warden scans during gameplay
- Alternatively, consider a proxy `d3d9.dll` approach (DLL placed in WoW directory, intercepts LoadLibrary)

### 3. WndProc Hook for Input

ImGui needs mouse/keyboard input. Subclass the game window via `SetWindowLongPtr(GWLP_WNDPROC)` or use a low-level hook:
- Forward WM_MOUSE*, WM_KEY*, WM_CHAR to `ImGui_ImplWin32_WndProcHandler`
- When the overlay is visible and capturing input, consume the messages (don't forward to WoW)
- Toggle overlay visibility with **F12** (edge-detected in `EndScene` via `GetAsyncKeyState(VK_F12)`)

### 4. Overlay Manager

Create `src/UnlockerAgent.Native/src/ImGuiOverlay.cpp` and `.h`:

```cpp
namespace TalosForge::ImGuiOverlay {
    bool Initialize(IDirect3DDevice9* device, HWND hwnd);
    void Shutdown();
    void Render();          // Called from EndScene hook
    void OnDeviceLost();    // Called before Reset
    void OnDeviceReset(IDirect3DDevice9* device); // Called after Reset
    bool IsVisible();
    void ToggleVisibility();
}
```

### 5. Debug Panels to Implement

**a) Player State Panel**
- HP / MaxHP bar with percentage
- Mana / MaxMana bar
- Level, Position (X, Y, Z), Facing
- Combat status, Casting status
- Target info (name, HP, distance)

**b) Object Browser Panel**
- Scrollable table of all nearby objects
- Columns: Type icon, Name/GUID, Level, HP%, Distance, Flags
- Click to target, right-click for details
- Filter: Units only, Players only, Dead/Alive, range slider

**c) Aura Inspector Panel**
- Player buffs/debuffs list (spell ID, stacks, remaining duration)
- Target buffs/debuffs
- Color-coded (green=buff, red=debuff)

**d) Bot State Panel**
- Current grind state (Idle/Travel/Pull/Combat/Loot/Rest)
- Kill count, loot count
- Navigation status, current waypoint
- Behavior tree status (current node, last result)
- Active plugin name and capabilities

**e) Event Log Panel**
- Scrolling log of recent events
- Filterable by category (Combat, Movement, Loot, Error)
- Timestamp + colored severity

**f) Control Panel**
- Start/Stop bot toggle
- Plugin selector dropdown
- Profile loader (file picker or text input for path)
- Navigation: click-on-map waypoint (stretch goal)

### 6. Performance Budget

- ImGui rendering must stay under 2ms per frame
- Use `ImGui::GetIO().DeltaTime` monitoring
- Skip rendering if DeltaTime spikes (WoW is loading/zoning)
- Limit object browser updates to every 5th frame if > 500 objects

---

## Build Instructions

The native agent builds with:
```powershell
powershell -File scripts/build-native-agent.ps1
```
This runs CMake with Visual Studio 2022, targeting Win32 (x86), static CRT, output is `d3dhelper.dll`.

After modifying `CMakeLists.txt` to add ImGui sources and `d3d9.lib`, rebuild with the same script.

If the DLL is locked by WoW, kill WoW first:
```powershell
Stop-Process -Name "Wow" -Force
```

---

## Integration Points

1. **Hook installation**: Call `ImGuiOverlay::Initialize` after `InstallDispatchTimer()` succeeds in the startup thread
2. **Hook removal**: Call `ImGuiOverlay::Shutdown()` in `UninstallDispatchTimer()` and agent shutdown
3. **Toggle hotkey**: Process in the WndProc hook, not via Lua
4. **Memory reads for display data**: Use the same SEH-protected patterns as `SehWalkObjects` in the existing code

---

## Files to Create

- `src/UnlockerAgent.Native/third_party/imgui/` — vendored ImGui source
- `src/UnlockerAgent.Native/src/ImGuiOverlay.cpp` — overlay manager
- `src/UnlockerAgent.Native/src/ImGuiOverlay.h` — public interface
- `src/UnlockerAgent.Native/src/D3D9Hook.cpp` — EndScene/Reset vtable hook
- `src/UnlockerAgent.Native/src/D3D9Hook.h` — hook interface
- `src/UnlockerAgent.Native/src/OverlayPanels.cpp` — all panel rendering code

## Files to Modify

- `src/UnlockerAgent.Native/CMakeLists.txt` — add ImGui sources, link d3d9.lib
- `src/UnlockerAgent.Native/src/UnlockerAgentExports.cpp` — call overlay init/shutdown at appropriate points

---

## Testing

1. Build the DLL
2. Launch WoW 3.3.5a from your client path (e.g. `Wow.exe`)
3. Log in with your own local test account credentials
4. Run the injection pipeline: `powershell -File scripts/test-injection-pipeline.ps1`
5. After injection (agent activates immediately when `kDeferredActivationMs` is 0), press **F12** to toggle the overlay
6. The ImGui overlay should appear over WoW, mouse should interact with it
7. Verify WoW remains playable underneath (no FPS tank, no kicks to character select)
