param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",
    [switch] $NoIncremental,
    [switch] $SkipTests,
    # Matches TalosForge.UI.csproj - use on machines without MSVC/CMake for managed-only build.
    [switch] $SkipNativeAgentBuild,
    # Live WoW + injected agent; default test run excludes TalosForge.Tests.Smoke.
    [switch] $IncludeSmoke
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

Write-Host "=== TalosForge rebuild ($Configuration) ===" -ForegroundColor Cyan
Write-Host "Repo: $repoRoot"

$buildArgs = @(
    "build",
    "TalosForge.sln",
    "-c", $Configuration
)
if ($NoIncremental) {
    $buildArgs += "--no-incremental"
}

$msbuildProps = @()
if ($SkipNativeAgentBuild) {
    $msbuildProps += "-p:SkipNativeAgentBuild=true"
}

Write-Host "dotnet $($buildArgs -join ' ') $($msbuildProps -join ' ')" -ForegroundColor DarkGray
& dotnet @buildArgs @msbuildProps
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (-not $SkipTests) {
    Write-Host "`n=== dotnet test ===" -ForegroundColor Cyan
    $testArgs = @("test", "TalosForge.sln", "-c", $Configuration, "--no-build", "--verbosity", "minimal")
    if (-not $IncludeSmoke) {
        $testArgs += @("--filter", "FullyQualifiedName!~TalosForge.Tests.Smoke")
        Write-Host "(excluding TalosForge.Tests.Smoke; use -IncludeSmoke after injecting WoW)" -ForegroundColor DarkGray
    }
    & dotnet @testArgs
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Host "`n=== Done ===" -ForegroundColor Green
if (-not $SkipNativeAgentBuild) {
    $dll = Join-Path $repoRoot "src\UI\TalosForge.UI\bin\$Configuration\net8.0-windows\d3dhelper.dll"
    if (Test-Path $dll) {
        Write-Host "d3dhelper.dll -> $dll"
    } else {
        Write-Host "Note: d3dhelper.dll not beside UI exe (native build skipped or copy failed)." -ForegroundColor Yellow
    }
}
