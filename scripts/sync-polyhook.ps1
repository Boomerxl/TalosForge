param(
    [string]$Destination = "C:/Utilities/TalosForge/third_party/PolyHook_2_0",
    [string]$Commit = "f4aee8e47383825469f924903357038b2efd8ca7"
)

$ErrorActionPreference = "Stop"

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    throw "git is required to sync PolyHook_2_0."
}

if (-not (Test-Path $Destination)) {
    New-Item -Path $Destination -ItemType Directory -Force | Out-Null
}

if (-not (Test-Path (Join-Path $Destination ".git"))) {
    Remove-Item -Path (Join-Path $Destination "*") -Recurse -Force -ErrorAction SilentlyContinue
    git clone https://github.com/stevemk14ebr/PolyHook_2_0 $Destination | Out-Host
}

git -C $Destination fetch --depth 1 origin $Commit | Out-Host
git -C $Destination checkout --force $Commit | Out-Host

Write-Host "PolyHook_2_0 synced at $Commit -> $Destination"
