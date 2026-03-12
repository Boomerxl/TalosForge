param(
    [ValidateSet("start", "status", "screenshot", "install-diagnostics", "anti-afk")]
    [string]$Action = "status",
    [string]$WowExePath = "C:\Games\Ebonhold\Wow.exe",
    [string]$WowArguments = "",
    [string]$WowErrorDir = "C:\Games\Ebonhold\Errors",
    [string]$Username = $env:TALOSFORGE_WOW_USERNAME,
    [string]$Password = $env:TALOSFORGE_WOW_PASSWORD,
    [string]$PipeName = "TalosForge.UnlockerAdapter.v1",
    [bool]$StartDevStack = $true,
    [ValidateSet("wow-agent")]
    [string]$BridgeMode = "wow-agent",
    [ValidateSet("native")]
    [string]$AgentRuntimeMode = "native",
    [int]$LaunchTimeoutSeconds = 45,
    [int]$LoginSettleSeconds = 8,
    [int]$EnterWorldInitialDelaySeconds = 12,
    [int]$WorldDetectTimeoutSeconds = 120,
    [int]$WorldEnterRetrySeconds = 15,
    [int]$MaxEnterWorldAttempts = 3,
    [int]$AntiAfkPulseSeconds = 45,
    [int]$AntiAfkMinutes = 30,
    [switch]$SkipLogin,
    [switch]$SkipDiagnostics
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$stateRoot = Join-Path $repoRoot "artifacts\wow-testbench"
$stateFile = Join-Path $stateRoot "state.json"
$devStateFile = Join-Path $repoRoot "artifacts\dev-stack\state.json"

function Ensure-UiAssemblies {
    Add-Type -AssemblyName System.Windows.Forms
    Add-Type -AssemblyName System.Drawing

    if (-not ("TalosForge.WowTestbench.NativeMethods" -as [type])) {
        Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

namespace TalosForge.WowTestbench
{
    public static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool ShowWindowAsync(IntPtr hWnd, int nCmdShow);
    }
}
"@
    }
}

function Read-TestbenchState {
    if (-not (Test-Path $stateFile)) {
        return $null
    }

    return Get-Content -Raw $stateFile | ConvertFrom-Json
}

function Write-TestbenchState([object]$state) {
    if (-not (Test-Path $stateRoot)) {
        New-Item -Path $stateRoot -ItemType Directory -Force | Out-Null
    }

    $state | ConvertTo-Json -Depth 8 | Set-Content -Path $stateFile -Encoding UTF8
}

function New-RunDirectory {
    if (-not (Test-Path $stateRoot)) {
        New-Item -Path $stateRoot -ItemType Directory -Force | Out-Null
    }

    $runId = Get-Date -Format "yyyyMMdd-HHmmss"
    $runDir = Join-Path $stateRoot ("run-" + $runId)
    New-Item -Path $runDir -ItemType Directory -Force | Out-Null
    return $runDir
}

function Get-WowProcess {
    return Get-Process -Name "Wow" -ErrorAction SilentlyContinue |
        Sort-Object StartTime -Descending |
        Select-Object -First 1
}

function Wait-ForWowWindow([int]$timeoutSeconds) {
    $deadline = (Get-Date).AddSeconds([Math]::Max(1, $timeoutSeconds))
    while ((Get-Date) -lt $deadline) {
        $proc = Get-WowProcess
        if ($null -ne $proc) {
            $proc.Refresh()
            if ($proc.MainWindowHandle -ne 0) {
                return $proc
            }
        }

        Start-Sleep -Milliseconds 500
    }

    return $null
}

function Escape-SendKeysText([string]$text) {
    if ([string]::IsNullOrEmpty($text)) {
        return ""
    }

    $builder = New-Object System.Text.StringBuilder
    foreach ($char in $text.ToCharArray()) {
        switch ($char) {
            '+' { [void]$builder.Append('{+}') }
            '^' { [void]$builder.Append('{^}') }
            '%' { [void]$builder.Append('{%}') }
            '~' { [void]$builder.Append('{~}') }
            '(' { [void]$builder.Append('{(}') }
            ')' { [void]$builder.Append('{)}') }
            '{' { [void]$builder.Append('{{}') }
            '}' { [void]$builder.Append('{}}') }
            '[' { [void]$builder.Append('{[}') }
            ']' { [void]$builder.Append('{]}') }
            default { [void]$builder.Append($char) }
        }
    }

    return $builder.ToString()
}

function Focus-WowWindow([System.Diagnostics.Process]$process) {
    Ensure-UiAssemblies

    $process.Refresh()
    if ($process.MainWindowHandle -eq 0) {
        throw "WoW main window is not available."
    }

    [TalosForge.WowTestbench.NativeMethods]::ShowWindowAsync($process.MainWindowHandle, 9) | Out-Null
    Start-Sleep -Milliseconds 150
    [System.Windows.Forms.SendKeys]::SendWait('%')
    Start-Sleep -Milliseconds 100
    [TalosForge.WowTestbench.NativeMethods]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 350
}

function Send-WowKeys([System.Diagnostics.Process]$process, [string]$keys, [int]$settleMs = 500) {
    Focus-WowWindow $process
    [System.Windows.Forms.SendKeys]::SendWait($keys)
    Start-Sleep -Milliseconds ([Math]::Max(50, $settleMs))
}

function Capture-WowWindow([System.Diagnostics.Process]$process, [string]$outputPath) {
    Ensure-UiAssemblies

    $process.Refresh()
    if ($process.MainWindowHandle -eq 0) {
        throw "WoW main window is not available for capture."
    }

    Focus-WowWindow $process
    Start-Sleep -Milliseconds 250

    $rect = New-Object TalosForge.WowTestbench.NativeMethods+RECT
    if (-not [TalosForge.WowTestbench.NativeMethods]::GetWindowRect($process.MainWindowHandle, [ref]$rect)) {
        throw "GetWindowRect failed for WoW window."
    }

    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top
    if ($width -le 0 -or $height -le 0) {
        throw "WoW window has invalid bounds ($width x $height)."
    }

    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, $bitmap.Size)
        $bitmap.Save($outputPath, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }

    return $outputPath
}

function Read-DevStackState {
    if (-not (Test-Path $devStateFile)) {
        return $null
    }

    return Get-Content -Raw $devStateFile | ConvertFrom-Json
}

function Get-CoreLogPath {
    $devState = Read-DevStackState
    if ($null -eq $devState) {
        return $null
    }

    return $devState.processes |
        Where-Object { $_.name -eq "core" } |
        Select-Object -ExpandProperty outLog -ErrorAction SilentlyContinue
}

function Get-LatestCoreSnapshotLine {
    $coreLog = Get-CoreLogPath
    if ([string]::IsNullOrWhiteSpace($coreLog) -or -not (Test-Path $coreLog)) {
        return $null
    }

    $match = Select-String -Path $coreLog -Pattern "snapshot tick=" -SimpleMatch -ErrorAction SilentlyContinue |
        Select-Object -Last 1
    if ($null -eq $match) {
        return $null
    }

    return $match.Line
}

function Test-InWorld {
    $line = Get-LatestCoreSnapshotLine
    if ([string]::IsNullOrWhiteSpace($line)) {
        return $false
    }

    return ($line -notmatch "player_guid=none") -and ($line -notmatch "success=False")
}

function Get-LatestWowCrashFile {
    if (-not (Test-Path $WowErrorDir)) {
        return $null
    }

    return Get-ChildItem -Path $WowErrorDir -Filter "*.txt" -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
}

function Send-LuaCommand([string]$lua) {
    $payload = ConvertTo-Json @{ code = $lua } -Compress
    $now = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $request = [ordered]@{
        version = 1
        commandId = [int64]$now
        opcode = "LuaDoString"
        opcodeValue = 1
        payloadJson = $payload
        timestampUnixMs = [int64]$now
    }

    $line = $request | ConvertTo-Json -Compress
    $client = [System.IO.Pipes.NamedPipeClientStream]::new(
        ".",
        $PipeName,
        [System.IO.Pipes.PipeDirection]::InOut,
        [System.IO.Pipes.PipeOptions]::Asynchronous)

    $writer = $null
    $reader = $null
    try {
        $client.Connect(3000)
        $writer = [System.IO.StreamWriter]::new($client, [System.Text.UTF8Encoding]::new($false), 4096, $true)
        $writer.AutoFlush = $true
        $reader = [System.IO.StreamReader]::new($client, [System.Text.Encoding]::UTF8, $false, 4096, $true)
        $writer.WriteLine($line)
        $responseLine = $reader.ReadLine()
        if ([string]::IsNullOrWhiteSpace($responseLine)) {
            throw "Adapter pipe returned an empty response."
        }

        $response = $responseLine | ConvertFrom-Json
        if (-not $response.success) {
            throw $response.message
        }

        return $response
    }
    finally {
        if ($null -ne $writer) { $writer.Dispose() }
        if ($null -ne $reader) { $reader.Dispose() }
        $client.Dispose()
    }
}

function Install-DiagnosticsOverlay {
    $lua = @"
if not _G.TalosForgeDiag then _G.TalosForgeDiag = { lastLuaError = '', errorCount = 0, lastLuaErrorAt = '' } end;
if not _G.TalosForgeDiagInstalled and _G.seterrorhandler and _G.geterrorhandler then
  local prev = geterrorhandler();
  seterrorhandler(function(msg)
    local diag = _G.TalosForgeDiag or {};
    diag.lastLuaError = tostring(msg or '');
    diag.errorCount = (tonumber(diag.errorCount) or 0) + 1;
    diag.lastLuaErrorAt = (date and date('%H:%M:%S')) or '';
    _G.TalosForgeDiag = diag;
    if prev then return prev(msg) end;
  end);
  _G.TalosForgeDiagInstalled = true;
end;
local frame = _G['TalosForgeBenchDiagFrame'];
if not frame then
  frame = CreateFrame('Frame', 'TalosForgeBenchDiagFrame', UIParent);
  frame:SetSize(500, 54);
  frame:SetPoint('TOPRIGHT', UIParent, 'TOPRIGHT', -20, -120);
  frame:SetFrameStrata('TOOLTIP');
  frame:SetFrameLevel(9999);
  local bg = frame:CreateTexture(nil, 'BACKGROUND');
  bg:SetAllPoints(true);
  bg:SetTexture(0, 0, 0, 0.72);
  frame.bg = bg;
  local text = frame:CreateFontString(nil, 'OVERLAY');
  text:SetPoint('TOPLEFT', frame, 'TOPLEFT', 10, -8);
  text:SetPoint('BOTTOMRIGHT', frame, 'BOTTOMRIGHT', -10, 8);
  text:SetJustifyH('LEFT');
  text:SetJustifyV('TOP');
  if GameFontHighlightSmall then text:SetFontObject(GameFontHighlightSmall) elseif GameFontNormalSmall then text:SetFontObject(GameFontNormalSmall) elseif ChatFontNormal then text:SetFontObject(ChatFontNormal) end;
  if not text:GetFont() then text:SetFont('Fonts\\FRIZQT__.TTF', 12, '') end;
  text:SetTextColor(0.95, 0.95, 0.75, 1);
  text:SetShadowOffset(1, -1);
  text:SetShadowColor(0, 0, 0, 1);
  frame.text = text;
end;
local diag = _G.TalosForgeDiag or {};
local msg = 'TalosForge Bench ' .. ((date and date('%H:%M:%S')) or '');
if diag.lastLuaError and diag.lastLuaError ~= '' then
  local trimmed = tostring(diag.lastLuaError);
  if string.len(trimmed) > 110 then trimmed = string.sub(trimmed, 1, 110) .. '...' end;
  msg = msg .. '\nLuaErr: ' .. trimmed;
else
  msg = msg .. '\nLuaErr: none';
end;
frame.text:SetText(msg);
frame:Show();
"@

    return Send-LuaCommand -lua $lua
}

function Invoke-AntiAfkPulse {
    $lua = @"
if UnitExists and UnitExists('player') then
  if UnitIsAFK and UnitIsAFK('player') and SendChatMessage then
    SendChatMessage('', 'AFK');
  end;
  if JumpOrAscendStart and AscendStop then
    local f = CreateFrame('Frame');
    local elapsed = 0;
    JumpOrAscendStart();
    f:SetScript('OnUpdate', function(self, e)
      elapsed = elapsed + e;
      if elapsed >= 0.08 then
        AscendStop();
        self:SetScript('OnUpdate', nil);
        self:Hide();
      end
    end);
  end;
end;
"@

    return Send-LuaCommand -lua $lua
}

function Start-OrReuseDevStack {
    if (-not $StartDevStack) {
        return
    }

    & (Join-Path $repoRoot "scripts\dev-stack.ps1") `
        -Action start `
        -BridgeMode $BridgeMode `
        -AgentRuntimeMode $AgentRuntimeMode `
        -EnableInGameUi:$true `
        -InGameUiInterval 1 | Out-Host
}

function Start-OrReuseWow {
    $existing = Get-WowProcess
    if ($null -ne $existing) {
        $existing.Refresh()
        if ($existing.MainWindowHandle -ne 0) {
            return $existing
        }
    }

    if (-not (Test-Path $WowExePath)) {
        throw "WoW executable not found: $WowExePath"
    }

    if ([string]::IsNullOrWhiteSpace($WowArguments)) {
        Start-Process -FilePath $WowExePath | Out-Null
    }
    else {
        Start-Process -FilePath $WowExePath -ArgumentList $WowArguments | Out-Null
    }

    $proc = Wait-ForWowWindow -timeoutSeconds $LaunchTimeoutSeconds
    if ($null -eq $proc) {
        throw "Timed out waiting for WoW window after $LaunchTimeoutSeconds second(s)."
    }

    return $proc
}

function Invoke-LoginSequence([System.Diagnostics.Process]$process) {
    if ($SkipLogin) {
        return
    }

    if ([string]::IsNullOrWhiteSpace($Username) -or [string]::IsNullOrWhiteSpace($Password)) {
        throw "Username/password are required unless -SkipLogin is used. Set TALOSFORGE_WOW_USERNAME and TALOSFORGE_WOW_PASSWORD or pass -Username/-Password."
    }

    $user = Escape-SendKeysText $Username
    $pass = Escape-SendKeysText $Password

    Send-WowKeys -process $process -keys ('^a' + $user) -settleMs 400
    Send-WowKeys -process $process -keys '{TAB}' -settleMs 350
    Send-WowKeys -process $process -keys ('^a' + $pass) -settleMs 400
    Send-WowKeys -process $process -keys '{ENTER}' -settleMs 500
}

function Wait-ForInWorld([System.Diagnostics.Process]$process, [string]$runDir) {
    $deadline = (Get-Date).AddSeconds([Math]::Max(10, $WorldDetectTimeoutSeconds))
    $attempts = 0
    $nextEnterAt = (Get-Date).AddSeconds([Math]::Max(1, $EnterWorldInitialDelaySeconds))

    while ((Get-Date) -lt $deadline) {
        if (Test-InWorld) {
            return $true
        }

        if ($attempts -lt $MaxEnterWorldAttempts -and (Get-Date) -ge $nextEnterAt) {
            $attempts++
            Send-WowKeys -process $process -keys '{ENTER}' -settleMs 700
            $shot = Join-Path $runDir ("enter-world-attempt-{0:00}.png" -f $attempts)
            Capture-WowWindow -process $process -outputPath $shot | Out-Null
            $nextEnterAt = (Get-Date).AddSeconds([Math]::Max(3, $WorldEnterRetrySeconds))
        }

        Start-Sleep -Seconds 2
    }

    return $false
}

function Show-Status {
    $benchState = Read-TestbenchState
    if ($null -ne $benchState) {
        Write-Host "Bench run:    $($benchState.runDir)"
        Write-Host "Started UTC:  $($benchState.startedUtc)"
        if ($benchState.lastScreenshot) {
            Write-Host "Screenshot:   $($benchState.lastScreenshot)"
        }
    }
    else {
        Write-Host "Bench run:    none"
    }

    $wow = Get-WowProcess
    if ($null -eq $wow) {
        Write-Host "WoW:          not running"
    }
    else {
        $wow.Refresh()
        Write-Host "WoW:          pid=$($wow.Id) handle=$($wow.MainWindowHandle)"
    }

    $snapshot = Get-LatestCoreSnapshotLine
    if ($snapshot) {
        Write-Host "Core snapshot:$snapshot"
    }
    else {
        Write-Host "Core snapshot:none"
    }

    $crash = Get-LatestWowCrashFile
    if ($null -ne $crash) {
        Write-Host "Latest crash: $($crash.FullName) ($($crash.LastWriteTime))"
    }
    else {
        Write-Host "Latest crash: none"
    }
}

switch ($Action) {
    "start" {
        $runDir = New-RunDirectory
        $state = [pscustomobject]@{
            startedUtc = [DateTimeOffset]::UtcNow.ToString("o")
            runDir = $runDir
            wowExePath = $WowExePath
            wowErrorDir = $WowErrorDir
            bridgeMode = $BridgeMode
            agentRuntimeMode = $AgentRuntimeMode
            lastScreenshot = $null
            lastCoreSnapshot = $null
        }
        Write-TestbenchState $state

        Start-OrReuseDevStack

        $wow = Start-OrReuseWow
        $state | Add-Member -NotePropertyName wowPid -NotePropertyValue $wow.Id -Force

        $launchShot = Join-Path $runDir "01-wow-window.png"
        Capture-WowWindow -process $wow -outputPath $launchShot | Out-Null
        $state.lastScreenshot = $launchShot
        Write-TestbenchState $state

        Invoke-LoginSequence -process $wow
        Start-Sleep -Seconds ([Math]::Max(1, $LoginSettleSeconds))
        $loginShot = Join-Path $runDir "02-after-login.png"
        Capture-WowWindow -process $wow -outputPath $loginShot | Out-Null
        $state.lastScreenshot = $loginShot
        Write-TestbenchState $state

        $inWorld = Wait-ForInWorld -process $wow -runDir $runDir
        $state.lastCoreSnapshot = Get-LatestCoreSnapshotLine

        if ($inWorld -and -not $SkipDiagnostics) {
            Install-DiagnosticsOverlay | Out-Null
            Start-Sleep -Seconds 2
        }

        $finalLabel = if ($inWorld) { "03-in-world.png" } else { "03-world-timeout.png" }
        $finalShot = Join-Path $runDir $finalLabel
        Capture-WowWindow -process $wow -outputPath $finalShot | Out-Null
        $state.lastScreenshot = $finalShot
        $state.inWorld = $inWorld
        Write-TestbenchState $state

        Show-Status
        if (-not $inWorld) {
            throw "WoW did not reach in-world state within $WorldDetectTimeoutSeconds second(s). Screenshot: $finalShot"
        }
    }
    "status" {
        Show-Status
    }
    "screenshot" {
        $wow = Get-WowProcess
        if ($null -eq $wow) {
            throw "WoW process not found."
        }

        $benchState = Read-TestbenchState
        $runDir = if ($benchState -and $benchState.runDir) { $benchState.runDir } else { New-RunDirectory }
        $shot = Join-Path $runDir ("manual-{0}.png" -f (Get-Date -Format "HHmmss"))
        Capture-WowWindow -process $wow -outputPath $shot | Out-Null

        if ($benchState) {
            $benchState.lastScreenshot = $shot
            $benchState.lastCoreSnapshot = Get-LatestCoreSnapshotLine
            Write-TestbenchState $benchState
        }

        Write-Host "Screenshot saved: $shot"
    }
    "install-diagnostics" {
        $response = Install-DiagnosticsOverlay
        Write-Host "Diagnostics installed: $($response.message)"
    }
    "anti-afk" {
        if ($AntiAfkPulseSeconds -lt 15) {
            throw "AntiAfkPulseSeconds must be >= 15."
        }

        if ($AntiAfkMinutes -lt 1) {
            throw "AntiAfkMinutes must be >= 1."
        }

        $wow = Get-WowProcess
        if ($null -eq $wow) {
            throw "WoW process not found."
        }

        $benchState = Read-TestbenchState
        $runDir = if ($benchState -and $benchState.runDir) { $benchState.runDir } else { New-RunDirectory }
        $deadline = (Get-Date).AddMinutes($AntiAfkMinutes)
        $pulse = 0

        Write-Host "Running anti-AFK loop for $AntiAfkMinutes minute(s) with $AntiAfkPulseSeconds second pulse interval."
        while ((Get-Date) -lt $deadline) {
            $pulse++
            $response = Invoke-AntiAfkPulse
            $shot = Join-Path $runDir ("anti-afk-{0:000}.png" -f $pulse)
            Capture-WowWindow -process $wow -outputPath $shot | Out-Null
            Write-Host ("pulse={0} ack={1} screenshot={2}" -f $pulse, $response.message, $shot)
            Start-Sleep -Seconds $AntiAfkPulseSeconds
        }
    }
    default {
        throw "Unsupported action: $Action"
    }
}
