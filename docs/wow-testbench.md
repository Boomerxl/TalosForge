# WoW Testbench

`scripts/wow-testbench.ps1` is a local operator harness for native `wow-agent` validation.

What it does:
- starts the TalosForge dev stack in `wow-agent` + `native` mode;
- launches `Wow.exe`;
- types login credentials and sends `Enter` to reach character select/in-world;
- watches Core snapshot logs to detect whether WoW is actually in-world;
- captures the WoW window to PNG artifacts so failures can be inspected without manual screenshots;
- installs a lightweight Lua diagnostics frame and preserves the latest crash-file location.

## Required environment

Set credentials once per shell:

```powershell
$env:TALOSFORGE_WOW_USERNAME = "your-account-name"
$env:TALOSFORGE_WOW_PASSWORD = "your-password"
```

Defaults:
- WoW exe: `C:\Games\Ebonhold\Wow.exe`
- WoW crash dir: `C:\Games\Ebonhold\Errors`
- adapter pipe: `TalosForge.UnlockerAdapter.v1`

## Common commands

Start full bench flow:

```powershell
./scripts/wow-testbench.ps1 -Action start
```

Status only:

```powershell
./scripts/wow-testbench.ps1 -Action status
```

Capture the current WoW window:

```powershell
./scripts/wow-testbench.ps1 -Action screenshot
```

Install/update the in-game diagnostics frame only:

```powershell
./scripts/wow-testbench.ps1 -Action install-diagnostics
```

Skip login typing when WoW is already past the login screen:

```powershell
./scripts/wow-testbench.ps1 -Action start -SkipLogin
```

## Artifacts

Bench artifacts are written under:

`artifacts/wow-testbench/run-<timestamp>/`

Typical files:
- `01-wow-window.png`
- `02-after-login.png`
- `enter-world-attempt-01.png`
- `03-in-world.png`
- `03-world-timeout.png`

Bench state file:
- `artifacts/wow-testbench/state.json`

## Notes

- The bench uses window focus + `SendKeys`, so WoW must be visible on the desktop.
- It does not bypass authentication or character selection logic; it automates the existing UI.
- Lua error capture is best-effort:
  - fatal client errors are read from `C:\Games\Ebonhold\Errors`;
  - non-fatal Lua issues are surfaced in the in-game TalosForge diagnostics text and visible in captured screenshots.
