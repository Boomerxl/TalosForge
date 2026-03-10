param(
    [switch]$IncludeDotnetClean,
    [switch]$WhatIf
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

Write-Host "Cleaning repo artifacts in: $repoRoot"

$targetNames = @("bin", "obj", "TestResults")
$targets = Get-ChildItem -Path $repoRoot -Directory -Recurse -Force |
    Where-Object { $targetNames -contains $_.Name }

if ($targets.Count -eq 0) {
    Write-Host "No build artifact directories found."
} else {
    foreach ($target in $targets) {
        if ($WhatIf) {
            Write-Host "[WhatIf] Remove $($target.FullName)"
            continue
        }

        Remove-Item -Path $target.FullName -Recurse -Force -ErrorAction Stop
        Write-Host "Removed $($target.FullName)"
    }
}

if ($IncludeDotnetClean) {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $dotnet) {
        Write-Host "dotnet not found on PATH; skipping dotnet clean."
    } else {
        foreach ($config in @("Debug", "Release")) {
            Write-Host "Running: dotnet clean TalosForge.sln -c $config"
            dotnet clean TalosForge.sln -c $config | Out-Host
        }
    }
}

$git = "C:\Users\houss\AppData\Local\GitHubDesktop\app-3.5.5\resources\app\git\cmd\git.exe"
if (Test-Path $git) {
    Write-Host ""
    Write-Host "Git status:"
    & $git status --short --branch | Out-Host
} else {
    Write-Host "Git executable not found at: $git"
}
