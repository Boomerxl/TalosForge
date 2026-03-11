param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$SkipPolyHookSync
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$sourceDir = Join-Path $repoRoot "src/UnlockerAgent.Native"
$polyHookDir = Join-Path $repoRoot "third_party/PolyHook_2_0"
$buildDir = Join-Path $repoRoot ("artifacts/native-agent/build/" + $Configuration)

if (-not (Test-Path $sourceDir)) {
    throw "Native agent source directory not found: $sourceDir"
}

if (-not $SkipPolyHookSync) {
    & (Join-Path $repoRoot "scripts/sync-polyhook.ps1") -Destination $polyHookDir | Out-Host
}

if (-not (Get-Command cmake -ErrorAction SilentlyContinue)) {
    throw "cmake is required to build UnlockerAgent.Native."
}

New-Item -Path $buildDir -ItemType Directory -Force | Out-Null

cmake -S $sourceDir -B $buildDir -A Win32 -DTALOSFORGE_POLYHOOK_DIR="$polyHookDir" -DTALOSFORGE_USE_POLYHOOK=ON | Out-Host
cmake --build $buildDir --config $Configuration | Out-Host

Write-Host "Native agent build completed."
Write-Host "Build dir: $buildDir"
