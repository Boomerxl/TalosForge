param(
    [ValidateSet("start", "stop", "status")]
    [string]$Action = "status",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [ValidateSet("mock", "process", "wow-cli", "wow-agent")]
    [string]$BridgeMode = "wow-agent",
    [string]$PipeName = "TalosForge.UnlockerAdapter.v1",
    [string]$AgentPipeName = "TalosForge.Agent.v1",
    [int]$AgentConnectTimeoutMs = 1200,
    [int]$AgentRequestTimeoutMs = 2500,
    [int]$AgentNativeConnectTimeoutMs = 8000,
    [ValidateSet("off", "standard", "full")]
    [string]$AgentEvasionProfile = "full",
    [ValidateSet("auto", "native", "simulated")]
    [string]$AgentRuntimeMode = "auto",
    [string]$AgentNativeDllPath = "",
    [string]$BridgeCommandPath = $env:TALOSFORGE_UNLOCKER_CLI_PATH,
    [string]$BridgeCommandArgs = $env:TALOSFORGE_UNLOCKER_CLI_ARGS,
    [bool]$EnableInGameUi = $true,
    [int]$InGameUiInterval = 1,
    [bool]$UseRealUnlocker = $true,
    [string]$SessionId = "",
    [switch]$Json
)

$ErrorActionPreference = "Stop"
$devStackPath = Join-Path $PSScriptRoot "dev-stack.ps1"
if (-not (Test-Path $devStackPath)) {
    throw "Missing supervisor backend script: $devStackPath"
}

function Invoke-DevStack([string]$DevAction, [switch]$OutputJson, [switch]$SuppressOutput) {
    $params = @{
        Action = $DevAction
        Configuration = $Configuration
        BridgeMode = $BridgeMode
        PipeName = $PipeName
        AgentPipeName = $AgentPipeName
        AgentConnectTimeoutMs = $AgentConnectTimeoutMs
        AgentRequestTimeoutMs = $AgentRequestTimeoutMs
        AgentNativeConnectTimeoutMs = $AgentNativeConnectTimeoutMs
        AgentEvasionProfile = $AgentEvasionProfile
        AgentRuntimeMode = $AgentRuntimeMode
        AgentNativeDllPath = $AgentNativeDllPath
        BridgeCommandPath = $BridgeCommandPath
        BridgeCommandArgs = $BridgeCommandArgs
        EnableInGameUi = $EnableInGameUi
        InGameUiInterval = $InGameUiInterval
        UseRealUnlocker = $UseRealUnlocker
    }

    if (-not [string]::IsNullOrWhiteSpace($SessionId)) {
        $params["SessionId"] = $SessionId
    }
    if ($OutputJson) {
        $params["OutputJson"] = $true
    }

    if ($SuppressOutput) {
        & $devStackPath @params *> $null
        return
    }

    & $devStackPath @params
}

if ($Json) {
    if ($Action -eq "start" -or $Action -eq "stop") {
        Invoke-DevStack -DevAction $Action -SuppressOutput
    }

    Invoke-DevStack -DevAction "status" -OutputJson
    return
}

Invoke-DevStack -DevAction $Action
