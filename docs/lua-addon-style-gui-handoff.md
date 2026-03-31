# TalosForge — In-Game Lua GUI (Addon-Style) — Handoff Prompt

## Purpose of this document

Give the next agent (or human) enough **context, constraints, and pointers** to design and implement a **polished, maintainable** in-game control surface for TalosForge on **WoW 3.3.5a (build 12340)** using **WoW’s Frame API** (same mental model as a **Load-on-Demand addon**, but driven by injected Lua via the unlocker — **not** a `Interface\AddOns\*.toc` addon unless you later add one).

**Goals for the UI:**

- **Debug:** world snapshot summaries, tick metrics, aura lists, target info, optional event log (align with existing `DebugOverlayService` / `DebugFrameLua`).
- **Plugins:** show loaded plugin names, capability flags, last error; toggles or “reload” if the host exposes them (may require host API work).
- **Scripts / Lua:** a safe way to run **small** snippets (or paste + execute) with clear warnings; map to `IBotContext.ExecuteLuaAsync` / `UnlockerOpcode.LuaDoString` on the host side.
- **Routines / profiles:** surface **grind profile** name, current state machine phase, behavior-tree status if the bot loop exposes it (`GrindProfile`, `BehaviorTreeRunner`, `BotEngine` — confirm what is observable from `BotRuntimeHost` / UI layer).

**Non-goals for v1 (unless you explicitly scope them):**

- Replacing the WPF **TalosForge.UI** desktop app; this is **in-game** chrome only.
- Full WeakAuras-style visual editor; prefer **clear panels + buttons + scroll areas**.

---

## Product constraints (read first)

1. **Rendering:** There is **no** D3D/ImGui overlay in the native agent anymore. UI is **100% WoW Lua frames** (`CreateFrame`, `FontString`, `Button`, `ScrollFrame`, templates like `UIPanelScrollFrameTemplate`, `UIPanelButtonTemplate`, etc.).
2. **Execution path:** Host (.NET) → **named pipe** → `d3dhelper.dll` → **SetTimer / game-thread** `lua_pcall` (`DispatchLuaOnGameThread` in `src/UnlockerAgent.Native/src/UnlockerAgentExports.cpp`). Large or frequent `LuaDoString` payloads cost latency; **throttle** updates (see `DebugOverlayService`: every 3rd tick).
3. **Script parity:** The **CreateFrame** body is duplicated for the inject-only bootstrap:
   - `src/Core/Debug/DebugFrameLua.cs` — `CreateFrame()` (C# host path).
   - `src/UnlockerAgent.Native/src/LuaFrameOverlay.cpp` — `GetCreateFrameScript()` (native bootstrap).
   **Any change to frame hierarchy or global names (`TF_Debug`, etc.) must stay in sync** (or extract a single shared `.lua` asset embedded in both — future refactor).

---

## Repo map — what already exists

| Area | Location | Notes |
|------|----------|--------|
| Lua templates (create / update / destroy) | `src/Core/Debug/DebugFrameLua.cs` | `UpdateState` builds `S('...')` lines into `TF_Debug.lines` |
| Overlay service (host pushes state) | `src/Core/Debug/DebugOverlayService.cs` | Uses `LuaDoString` with JSON `{ code: "..." }`; throttled updates |
| Native bootstrap (first frame after inject) | `src/UnlockerAgent.Native/src/LuaFrameOverlay.cpp` | Same create script as Core; no C# required for first paint |
| Opcodes available to plugins / host | `src/Core/Models/UnlockerContracts.cs` — `UnlockerOpcode` | `LuaDoString`, `LuaQuery`, movement, cast, interact, `QuerySpellInfo`, `QueryBags`, `QueryAuras`, … |
| Plugin API | `src/Core/Plugins/IBotContext.cs`, `BotContext.cs` | `ExecuteLuaAsync`, `QueryLuaAsync`, casts, move, target, … |
| Example plugin | `src/Plugins/SampleCombatPlugin/SampleCombatPlugin.cs` | Reference for capabilities and tick contract |
| Plugin loader | `src/Core/Plugins/PluginHost.cs` | Loads assemblies from a directory |
| Grind profiles | `src/Core/Profiles/GrindProfile.cs`, `ProfileLoader.cs` | JSON grind profiles |
| Behavior trees | `src/Core/BehaviorTree/*.cs` | Composites, leaves, runner |
| Historical ImGui doc | `docs/imgui-gui-handoff.md` | **Deprecated** path; Lua-only now |

---

## WoW 3.3.5a UI — addon-style knowledge to apply

Treat this like writing an addon **without** a `.toc`: you still use:

- **Parenting:** `UIParent`, strata (`FRAMESTRATA_HIGH` / `DIALOG` for floating tools).
- **Layout:** `SetPoint`, `SetSize`, anchors; **scroll frames** for long content.
- **Visuals:** `SetBackdrop` + `Interface\DialogFrame\...` textures (paths use `\\` in Lua strings), or **color overlays** + borders.
- **Typography:** `GameFontNormal`, `GameFontHighlightSmall`, `GameFontNormalLarge`, custom `FontString` colors via `|cffrrggbb` and `SetTextColor`.
- **Input:** `EnableMouse`, `RegisterForDrag`, `Button` `SetScript("OnClick", ...)`.
- **Tabs:** Either fake tabs with multiple `Button`s toggling child frame visibility, or reuse **Blizzard tab templates** if appropriate for 3.3.5a (verify template names on that client).

**Performance:** avoid creating frames every frame; **create once**, then **update text** (`SetText`, show/hide children). Match how `DebugFrameLua.UpdateState` fills pre-created `TF_Debug.lines[i]`.

---

## External references (inspiration, not copy-paste)

Use these to align with **community addon UX** and API usage (always verify against **3.3.5a** docs):

- **FrameXML / API:** WoWpedia or legacy WoWWiki pages for **Widget API** (Frame, Button, ScrollFrame) for **Wrath / 3.3.5**.
- **Patterns:** Study **AceGUI**-style layouts (tabs, tree groups) conceptually — you do not need to embed Ace3 in the unlocker unless you want the dependency; often **simple custom frames** are enough.
- **GitHub search ideas:** `CreateFrame UIParent` + `ScrollFrame` in Lua repos filtered by **WotLK** or **3.3.5**; **WeakAuras** / **DBM** architecture is overkill but shows production-grade layout patterns (do not copy their code blindly; licenses differ).

**Suggested approach for “find a project to work from”:**

1. Search GitHub for **GPL-2.0 or MIT** small WoW addons targeting **3.3.5** UI.
2. Extract **layout ideas** (padding, tab strip, scroll child height) and **texture choices**, not proprietary art.
3. Reimplement in TalosForge-owned Lua strings so licensing stays clean.

---

## Suggested architecture for a “decent” TalosForge GUI

### 1. Single root frame with named children

- e.g. `TalosForgeHub` parent, children `TF_TabDebug`, `TF_TabPlugins`, `TF_TabScript`, `TF_TabRoutine`.
- **Minimize globals:** one table `_G.TalosForgeUI = { ... }` holding frame refs and `Refresh*` functions.

### 2. C# side: extend or replace `DebugFrameLua`

- **Option A (incremental):** Grow `DebugFrameLua` with `CreateHubFrame()`, `UpdateHub_Debug(...)`, etc., and call from a new `TalosForgeOverlayService` that subsumes `DebugOverlayService`.
- **Option B (cleaner):** New `TalosForge/Gui/TalosForgeFrameLua.cs` with all templates; keep `DebugFrameLua` as a thin alias or delete after migration.

### 3. Host → game updates

- Reuse `UnlockerCommand` + `LuaDoString` pattern from `DebugOverlayService.MakeLuaCommand`.
- **Batch** multi-line updates into **one** `lua_pcall` where possible.
- **Rate-limit** (e.g. 5–10 Hz for text panels; slower for heavy sections).

### 4. Game → host interactions (gap to plan)

Today the pipe is primarily **host-initiated**. If buttons in WoW must **start/stop bot**, **run a script**, or **reload plugins**, you need one of:

- **Polling:** periodic `LuaQuery` reads `_G.TalosForgeUI.pendingAction` set by `OnClick` (simplest).
- **New opcode / shared flag file** (more work in native + host).

Document the chosen approach in code comments when implemented.

---

## Verification checklist

- [ ] **3.3.5a client:** frame appears, no Lua errors in `/script` or `DEFAULT_CHAT_FRAME` (you may add a dev-only `print` guard).
- [ ] **Inject path:** after inject, native `LuaFrameOverlay` and host `CreateFrame` do not fight (same global names — use **idempotent** `if TalosForgeHub then return end`).
- [ ] **Sync:** `LuaFrameOverlay.cpp` and `DebugFrameLua.cs` updated together or shared asset.
- [ ] **Load:** no per-tick `CreateFrame`; only attribute updates.
- [ ] **Security / abuse:** script panel warns users; avoid unbounded string execution from untrusted sources (product decision).

---

## Prompt you can paste into a new agent session

> You are working in `c:\Utilities\TalosForge`. Implement an **addon-style in-game GUI** for WoW **3.3.5a** using **Lua frames only** (no GPU hooks). Read `docs/lua-addon-style-gui-handoff.md`, then inspect `DebugFrameLua.cs`, `DebugOverlayService.cs`, `LuaFrameOverlay.cpp`, `UnlockerContracts.cs`, and `IBotContext.cs`. Design a **multi-tab** hub for **debug metrics**, **plugin status**, **Lua script runner**, and **routine/profile** visibility. Follow WoW Frame API best practices; throttle `LuaDoString` updates. Keep `LuaFrameOverlay.cpp` in sync with any new `CreateFrame` script. If in-game buttons must reach the .NET host, prefer a **polling** pattern via `LuaQuery` or document a new IPC mechanism. Build incrementally and verify in-game.

---

## Related docs

- `docs/imgui-gui-handoff.md` — historical ImGui attempt; **not** current stack.
- Native agent behavior — `src/UnlockerAgent.Native/src/UnlockerAgentExports.cpp` (timer dispatch, pipe protocol).
