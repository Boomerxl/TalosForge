param(
    [ValidateSet("start", "stop", "status")]
    [string]$Action = "start",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [ValidateSet("mock", "process", "wow-cli", "wow-agent")]
    [string]$BridgeMode = "mock",
    [string]$PipeName = "TalosForge.UnlockerAdapter.v1",
    [string]$AgentPipeName = "TalosForge.Agent.v1",
    [int]$AgentConnectTimeoutMs = 1200,
    [int]$AgentRequestTimeoutMs = 2500,
    [ValidateSet("off", "standard", "full")]
    [string]$AgentEvasionProfile = "full",
    [ValidateSet("auto", "native", "simulated")]
    [string]$AgentRuntimeMode = "auto",
    [string]$AgentNativeDllPath = "",
    [string]$BridgeCommandPath = $env:TALOSFORGE_UNLOCKER_CLI_PATH,
    [string]$BridgeCommandArgs = $env:TALOSFORGE_UNLOCKER_CLI_ARGS,
    [bool]$EnableInGameUi = $true,
    [int]$InGameUiInterval = 1,
    [bool]$UseRealUnlocker = $true
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$stateDir = Join-Path $repoRoot "artifacts/dev-stack"
$stateFile = Join-Path $stateDir "state.json"

function Get-ManagedProcesses {
    $pattern = "TalosForge\.(AdapterBridge|UnlockerHost|UnlockerAgentHost|Core)\.csproj|TalosForge\.(AdapterBridge|UnlockerHost|UnlockerAgentHost|Core)\.exe"
    return Get-CimInstance Win32_Process -Filter "Name='dotnet.exe' OR Name='TalosForge.AdapterBridge.exe' OR Name='TalosForge.UnlockerHost.exe' OR Name='TalosForge.UnlockerAgentHost.exe' OR Name='TalosForge.Core.exe'" |
        Where-Object {
            ($_.Name -eq "dotnet.exe" -and $_.CommandLine -match $pattern) -or
            ($_.Name -ne "dotnet.exe" -and $_.Name -match "TalosForge\.(AdapterBridge|UnlockerHost|UnlockerAgentHost|Core)\.exe")
        }
}

function Read-State {
    if (-not (Test-Path $stateFile)) {
        return $null
    }

    return Get-Content -Raw $stateFile | ConvertFrom-Json
}

function Write-State([object]$state) {
    if (-not (Test-Path $stateDir)) {
        New-Item -Path $stateDir -ItemType Directory -Force | Out-Null
    }

    $state | ConvertTo-Json -Depth 6 | Set-Content -Path $stateFile -Encoding UTF8
}

function Stop-ProcessIfRunning([int]$processId, [string]$name) {
    try {
        $proc = Get-Process -Id $processId -ErrorAction SilentlyContinue
        if ($null -ne $proc) {
            Stop-Process -Id $processId -Force
            Write-Host "Stopped $name (PID $processId)."
        }
    }
    catch {
        Write-Warning "Failed to stop $name (PID $processId): $($_.Exception.Message)"
    }
}

function Stop-Stack {
    $state = Read-State
    if ($null -eq $state) {
        $orphans = @(Get-ManagedProcesses)
        if ($orphans.Count -eq 0) {
            Write-Host "No dev stack state found. Nothing to stop."
            return
        }

        Write-Host "No state file found. Stopping orphan TalosForge runtime processes..."
        foreach ($proc in $orphans) {
            Stop-ProcessIfRunning -processId $proc.ProcessId -name $proc.Name
        }

        Write-Host "Orphan process cleanup complete."
        return
    }

    foreach ($entry in @($state.processes)) {
        Stop-ProcessIfRunning -processId $entry.pid -name $entry.name
    }

    Remove-Item -Path $stateFile -Force -ErrorAction SilentlyContinue
    Write-Host "Dev stack stopped."
}

function Start-Stack {
    if ($InGameUiInterval -lt 1) {
        throw "InGameUiInterval must be >= 1."
    }

    $unlockerCliProject = Join-Path $repoRoot "src/UnlockerCli/TalosForge.UnlockerCli.csproj"
    $unlockerCliExe = Join-Path $repoRoot ("src/UnlockerCli/bin/" + $Configuration + "/net8.0/TalosForge.UnlockerCli.exe")

    if ($BridgeMode -eq "wow-cli" -and [string]::IsNullOrWhiteSpace($BridgeCommandPath)) {
        if (Test-Path $unlockerCliProject) {
            if (-not (Test-Path $unlockerCliExe)) {
                Write-Host "Building in-repo unlocker CLI for wow-cli mode..."
                dotnet build $unlockerCliProject -c $Configuration | Out-Host
            }

            if (Test-Path $unlockerCliExe) {
                $BridgeCommandPath = $unlockerCliExe
                Write-Host "Using in-repo wow-cli command path: $BridgeCommandPath"
            }
        }
    }

    if (($BridgeMode -eq "process" -or $BridgeMode -eq "wow-cli") -and [string]::IsNullOrWhiteSpace($BridgeCommandPath)) {
        throw "BridgeCommandPath is required when BridgeMode is '$BridgeMode'. Pass -BridgeCommandPath or set TALOSFORGE_UNLOCKER_CLI_PATH."
    }

    if ($BridgeMode -eq "wow-cli") {
        Write-Warning "BridgeMode=wow-cli is experimental and may crash live clients. Prefer BridgeMode=wow-agent."
    }

    if ($BridgeMode -eq "mock" -and $EnableInGameUi -and $UseRealUnlocker) {
        Write-Warning "BridgeMode=mock only ACKs commands and does not execute in-game Lua. Overlay will not be visible in WoW."
    }

    $existing = Read-State
    if ($null -ne $existing) {
        Write-Host "Existing dev stack state found. Stopping previous stack first..."
        Stop-Stack
    }
    else {
        $orphans = @(Get-ManagedProcesses)
        if ($orphans.Count -gt 0) {
            Write-Host "Stopping orphan TalosForge runtime processes before startup..."
            foreach ($proc in $orphans) {
                Stop-ProcessIfRunning -processId $proc.ProcessId -name $proc.Name
            }
        }
    }

    if (-not (Test-Path $stateDir)) {
        New-Item -Path $stateDir -ItemType Directory -Force | Out-Null
    }

    $runId = Get-Date -Format "yyyyMMdd-HHmmss"
    $runDir = Join-Path $stateDir ("run-" + $runId)
    New-Item -Path $runDir -ItemType Directory -Force | Out-Null

    $bridgeOut = Join-Path $runDir "bridge.out.log"
    $bridgeErr = Join-Path $runDir "bridge.err.log"
    $agentOut = Join-Path $runDir "agent.out.log"
    $agentErr = Join-Path $runDir "agent.err.log"
    $hostOut = Join-Path $runDir "host.out.log"
    $hostErr = Join-Path $runDir "host.err.log"
    $coreOut = Join-Path $runDir "core.out.log"
    $coreErr = Join-Path $runDir "core.err.log"

    Set-Location $repoRoot

    $bridgeProject = Join-Path $repoRoot "src/AdapterBridge/TalosForge.AdapterBridge.csproj"
    $agentProject = Join-Path $repoRoot "src/UnlockerAgentHost/TalosForge.UnlockerAgentHost.csproj"
    $hostProject = Join-Path $repoRoot "src/UnlockerHost/TalosForge.UnlockerHost.csproj"
    $coreProject = Join-Path $repoRoot "src/Core/TalosForge.Core.csproj"

    $bridgeArgs = @(
        "run",
        "--project", $bridgeProject,
        "-c", $Configuration,
        "--",
        "--mode", $BridgeMode,
        "--pipe-name", $PipeName
    )
    if ($BridgeMode -eq "wow-agent") {
        $bridgeArgs += @(
            "--agent-pipe", $AgentPipeName,
            "--agent-connect-timeout-ms", $AgentConnectTimeoutMs,
            "--agent-request-timeout-ms", $AgentRequestTimeoutMs,
            "--agent-evasion-profile", $AgentEvasionProfile
        )
    }
    if (-not [string]::IsNullOrWhiteSpace($BridgeCommandPath)) {
        $bridgeArgs += @("--command-path", $BridgeCommandPath)
    }
    if (-not [string]::IsNullOrWhiteSpace($BridgeCommandArgs)) {
        $bridgeArgs += @("--command-args", $BridgeCommandArgs)
    }

    $hostArgs = @(
        "run",
        "--project", $hostProject,
        "-c", $Configuration,
        "--",
        "--executor", "adapter",
        "--adapter-backend", "pipe",
        "--adapter-pipe", $PipeName
    )

    $coreArgs = @(
        "run",
        "--project", $coreProject,
        "-c", $Configuration,
        "--"
    )
    if ($UseRealUnlocker) {
        $coreArgs += "--real-unlocker"
    }
    else {
        $coreArgs += "--use-mock-unlocker"
    }

    if ($EnableInGameUi) {
        $coreArgs += @("--ingame-ui", "--ingame-ui-interval", $InGameUiInterval)
    }

    $agentArgs = @(
        "run",
        "--project", $agentProject,
        "-c", $Configuration,
        "--",
        "--pipe-name", $AgentPipeName,
        "--runtime-mode", $AgentRuntimeMode,
        "--request-timeout-ms", $AgentRequestTimeoutMs,
        "--retry-count", 2,
        "--backoff-base-ms", 100,
        "--backoff-max-ms", $AgentRequestTimeoutMs,
        "--evasion-profile", $AgentEvasionProfile
    )
    if (-not [string]::IsNullOrWhiteSpace($AgentNativeDllPath)) {
        $agentArgs += @("--native-dll-path", $AgentNativeDllPath)
    }

    $started = @()
    try {
        if ($BridgeMode -eq "wow-agent") {
            $agentProcess = Start-Process -FilePath "dotnet" -ArgumentList $agentArgs -WorkingDirectory $repoRoot -PassThru -RedirectStandardOutput $agentOut -RedirectStandardError $agentErr
            $started += [pscustomobject]@{ Name = "agent"; Pid = $agentProcess.Id }
            Start-Sleep -Seconds 2
        }

        $bridgeProcess = Start-Process -FilePath "dotnet" -ArgumentList $bridgeArgs -WorkingDirectory $repoRoot -PassThru -RedirectStandardOutput $bridgeOut -RedirectStandardError $bridgeErr
        $started += [pscustomobject]@{ Name = "bridge"; Pid = $bridgeProcess.Id }
        Start-Sleep -Seconds 2

        $hostProcess = Start-Process -FilePath "dotnet" -ArgumentList $hostArgs -WorkingDirectory $repoRoot -PassThru -RedirectStandardOutput $hostOut -RedirectStandardError $hostErr
        $started += [pscustomobject]@{ Name = "host"; Pid = $hostProcess.Id }
        Start-Sleep -Seconds 2

        $coreProcess = Start-Process -FilePath "dotnet" -ArgumentList $coreArgs -WorkingDirectory $repoRoot -PassThru -RedirectStandardOutput $coreOut -RedirectStandardError $coreErr
        $started += [pscustomobject]@{ Name = "core"; Pid = $coreProcess.Id }
    }
    catch {
        foreach ($entry in $started) {
            Stop-ProcessIfRunning -processId $entry.Pid -name $entry.Name
        }

        throw "Failed to start dev stack: $($_.Exception.Message)"
    }

    $state = [pscustomobject]@{
        startedUtc = [DateTimeOffset]::UtcNow.ToString("o")
        configuration = $Configuration
        bridgeMode = $BridgeMode
        pipeName = $PipeName
        agentPipeName = $AgentPipeName
        runDir = $runDir
        processes = @(
            if ($BridgeMode -eq "wow-agent") {
                [pscustomobject]@{ name = "agent"; pid = $agentProcess.Id; outLog = $agentOut; errLog = $agentErr }
            }
            [pscustomobject]@{ name = "bridge"; pid = $bridgeProcess.Id; outLog = $bridgeOut; errLog = $bridgeErr },
            [pscustomobject]@{ name = "host"; pid = $hostProcess.Id; outLog = $hostOut; errLog = $hostErr },
            [pscustomobject]@{ name = "core"; pid = $coreProcess.Id; outLog = $coreOut; errLog = $coreErr }
        )
    }
    Write-State $state

    Write-Host "Dev stack started."
    if ($BridgeMode -eq "wow-agent") {
        Write-Host "Agent PID:  $($agentProcess.Id)"
    }
    Write-Host "Bridge PID: $($bridgeProcess.Id)"
    Write-Host "Host PID:   $($hostProcess.Id)"
    Write-Host "Core PID:   $($coreProcess.Id)"
    Write-Host ""
    Write-Host "Logs:"
    if ($BridgeMode -eq "wow-agent") {
        Write-Host "  $agentOut"
    }
    Write-Host "  $bridgeOut"
    Write-Host "  $hostOut"
    Write-Host "  $coreOut"
    Write-Host ""
    Write-Host "Use '.\\scripts\\dev-stack.ps1 -Action status' to check process health."
    Write-Host "Use '.\\scripts\\dev-stack.ps1 -Action stop' to stop all three."
}

function Show-Status {
    $state = Read-State
    if ($null -eq $state) {
        Write-Host "No dev stack is running (state file not found)."
        return
    }

    Write-Host "Dev stack started at $($state.startedUtc)"
    Write-Host "Configuration: $($state.configuration)"
    Write-Host "Bridge mode:   $($state.bridgeMode)"
    Write-Host "Pipe name:     $($state.pipeName)"
    if ($state.PSObject.Properties.Name -contains "agentPipeName") {
        Write-Host "Agent pipe:    $($state.agentPipeName)"
    }
    if ($state.PSObject.Properties.Name -contains "runDir") {
        Write-Host "Run logs dir:  $($state.runDir)"
    }
    Write-Host ""

    foreach ($entry in @($state.processes)) {
        $proc = Get-Process -Id $entry.pid -ErrorAction SilentlyContinue
        $status = if ($null -ne $proc) { "running" } else { "not running" }
        Write-Host ("{0,-6} pid={1,-8} status={2}" -f $entry.name, $entry.pid, $status)
        Write-Host ("        out={0}" -f $entry.outLog)
        Write-Host ("        err={0}" -f $entry.errLog)
    }
}

switch ($Action) {
    "start" { Start-Stack }
    "stop" { Stop-Stack }
    "status" { Show-Status }
    default { throw "Unsupported action: $Action" }
}
