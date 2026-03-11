param(
    [int]$DurationMinutes = 30,
    [int]$PollSeconds = 30,
    [ValidateSet("auto", "native", "simulated")]
    [string]$AgentRuntimeMode = "auto"
)

$ErrorActionPreference = "Stop"

if ($DurationMinutes -lt 1) {
    throw "DurationMinutes must be >= 1."
}

if ($PollSeconds -lt 5) {
    throw "PollSeconds must be >= 5."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

./scripts/dev-stack.ps1 -Action stop | Out-Host
./scripts/dev-stack.ps1 -Action start -BridgeMode wow-agent -AgentRuntimeMode $AgentRuntimeMode | Out-Host

$state = Get-Content -Raw .\artifacts\dev-stack\state.json | ConvertFrom-Json
$runDir = $state.runDir
$hostLog = Join-Path $runDir "host.out.log"
$agentLog = Join-Path $runDir "agent.out.log"
$coreLog = Join-Path $runDir "core.out.log"

$end = (Get-Date).AddMinutes($DurationMinutes)
Write-Host "Running soak for $DurationMinutes minute(s) (poll=$PollSeconds s)..."
Write-Host "Logs: $runDir"

while ((Get-Date) -lt $end) {
    ./scripts/dev-stack.ps1 -Action status | Out-Host
    if (Test-Path $hostLog) {
        Get-Content -Path $hostLog -Tail 3 | Out-Host
    }
    Start-Sleep -Seconds $PollSeconds
}

Write-Host ""
Write-Host "Soak finished. Final tails:"
if (Test-Path $agentLog) { Get-Content -Path $agentLog -Tail 20 | Out-Host }
if (Test-Path $hostLog) { Get-Content -Path $hostLog -Tail 20 | Out-Host }
if (Test-Path $coreLog) { Get-Content -Path $coreLog -Tail 20 | Out-Host }

./scripts/dev-stack.ps1 -Action stop | Out-Host
