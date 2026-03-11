param(
    [string]$PipeName = "TalosForge.UnlockerAdapter.v1",
    [string]$Message = "TALOSFORGE NATIVE PROBE",
    [int]$Repeat = 6,
    [int]$IntervalMs = 500
)

$ErrorActionPreference = "Stop"

if ($Repeat -lt 1) {
    throw "Repeat must be >= 1."
}

if ($IntervalMs -lt 100) {
    throw "IntervalMs must be >= 100."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$statePath = Join-Path $repoRoot "artifacts/dev-stack/state.json"
if (-not (Test-Path $statePath)) {
    throw "Dev stack state not found. Start stack first with ./scripts/dev-stack.ps1 -Action start -BridgeMode wow-agent -AgentRuntimeMode native"
}

$state = Get-Content -Raw $statePath | ConvertFrom-Json
$agentOut = $state.processes | Where-Object { $_.name -eq "agent" } | Select-Object -ExpandProperty outLog -ErrorAction SilentlyContinue
if ($agentOut -and (Test-Path $agentOut)) {
    $modeLine = Select-String -Path $agentOut -Pattern "Agent runtime mode=" -SimpleMatch -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($modeLine) {
        Write-Host $modeLine.Line
    }
}

for ($i = 1; $i -le $Repeat; $i++) {
    $stamp = Get-Date -Format "HH:mm:ss"
    $overlayText = "$Message [$i/$Repeat] $stamp"
    $safeText = $overlayText.Replace("'", "\\'")
    $lua = "local f=_G['TalosForgeManualProbe']; if not f then f=CreateFrame('Frame','TalosForgeManualProbe',UIParent); f:SetSize(1400,140); f:SetPoint('TOP',UIParent,'TOP',0,-130); f:SetFrameStrata('TOOLTIP'); f:SetFrameLevel(9999); local bg=f:CreateTexture(nil,'BACKGROUND'); bg:SetAllPoints(true); bg:SetColorTexture(0,0,0,0.7); f.bg=bg; local t=f:CreateFontString(nil,'OVERLAY','GameFontNormalHuge'); t:SetAllPoints(true); t:SetJustifyH('CENTER'); t:SetTextColor(1,0.2,0.2,1); t:SetShadowOffset(2,-2); t:SetShadowColor(0,0,0,1); f.t=t; end; f.t:SetText('" + $safeText + "'); f:Show(); UIErrorsFrame:AddMessage('" + $safeText + "',1,0.2,0.2,1);"
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

    try {
        $client.Connect(3000)
        $writer = [System.IO.StreamWriter]::new($client, [System.Text.UTF8Encoding]::new($false), 4096, $true)
        $writer.AutoFlush = $true
        $reader = [System.IO.StreamReader]::new($client, [System.Text.Encoding]::UTF8, $false, 4096, $true)

        $writer.WriteLine($line)
        $responseLine = $reader.ReadLine()
        if ([string]::IsNullOrWhiteSpace($responseLine)) {
            throw "Empty response from adapter pipe."
        }

        $response = $responseLine | ConvertFrom-Json
        if (-not $response.success) {
            throw "Probe failed: $($response.message)"
        }

        Write-Host "probe[$i/$Repeat] ok: $($response.message)"
    }
    finally {
        if ($null -ne $writer) { $writer.Dispose() }
        if ($null -ne $reader) { $reader.Dispose() }
        $client.Dispose()
    }

    if ($i -lt $Repeat) {
        Start-Sleep -Milliseconds $IntervalMs
    }
}

Write-Host "Probe complete. If WoW is running in-game, a red banner and UIErrors text should be visible."
Write-Host "Run logs: $($state.runDir)"
