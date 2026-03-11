param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$Generator = "Visual Studio 17 2022",
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

function Resolve-CMakePath {
    $cmakeCommand = Get-Command cmake -ErrorAction SilentlyContinue
    if ($cmakeCommand) {
        return $cmakeCommand.Source
    }

    $fallback = "C:\Program Files\CMake\bin\cmake.exe"
    if (Test-Path $fallback) {
        return $fallback
    }

    throw "cmake is required to build UnlockerAgent.Native."
}

function Test-RegisteredVisualStudioInstance {
    $vsWhere = "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $vsWhere)) {
        return $false
    }

    $installPath = & $vsWhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    return -not [string]::IsNullOrWhiteSpace($installPath)
}

function Import-VcVars32Environment {
    $vcVars = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\VC\Auxiliary\Build\vcvars32.bat"
    if (-not (Test-Path $vcVars)) {
        throw "vcvars32.bat not found at $vcVars"
    }

    $envLines = & cmd.exe /c "`"$vcVars`" >nul && set"
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to initialize MSVC x86 environment via vcvars32.bat."
    }

    foreach ($line in $envLines) {
        $idx = $line.IndexOf("=")
        if ($idx -gt 0) {
            $name = $line.Substring(0, $idx)
            $value = $line.Substring($idx + 1)
            Set-Item -Path "Env:$name" -Value $value
        }
    }
}

function Resolve-WindowsSdkBinX86 {
    $sdkRoot = "C:\Program Files (x86)\Windows Kits\10\bin"
    if (-not (Test-Path $sdkRoot)) {
        throw "Windows SDK bin directory not found at $sdkRoot"
    }

    $candidates = Get-ChildItem -Path $sdkRoot -Directory -ErrorAction SilentlyContinue |
        Where-Object { Test-Path (Join-Path $_.FullName "x86\rc.exe") } |
        Sort-Object Name -Descending

    if (-not $candidates) {
        throw "Windows SDK x86 tools (rc.exe) were not found under $sdkRoot"
    }

    return Join-Path $candidates[0].FullName "x86"
}

$cmakePath = Resolve-CMakePath
$resolvedGenerator = $Generator
if ($resolvedGenerator -eq "Visual Studio 17 2022" -and -not (Test-RegisteredVisualStudioInstance)) {
    Write-Warning "Visual Studio instance is not registered with vswhere; falling back to NMake Makefiles."
    $resolvedGenerator = "NMake Makefiles"
}

New-Item -Path $buildDir -ItemType Directory -Force | Out-Null

$cacheFile = Join-Path $buildDir "CMakeCache.txt"
if (Test-Path $cacheFile) {
    $cacheGeneratorLine = Select-String -Path $cacheFile -Pattern "^CMAKE_GENERATOR:INTERNAL=" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($cacheGeneratorLine) {
        $cacheGenerator = $cacheGeneratorLine.Line.Split("=", 2)[1]
        if ($cacheGenerator -and -not $cacheGenerator.Equals($resolvedGenerator, [StringComparison]::OrdinalIgnoreCase)) {
            Write-Host "Detected cached generator '$cacheGenerator'; cleaning stale CMake cache for '$resolvedGenerator'."
            Remove-Item -Path $cacheFile -Force -ErrorAction SilentlyContinue
            Remove-Item -Path (Join-Path $buildDir "CMakeFiles") -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

if ($resolvedGenerator -eq "NMake Makefiles") {
    Import-VcVars32Environment
    $sdkBinX86 = Resolve-WindowsSdkBinX86
    $rcPath = (Join-Path $sdkBinX86 "rc.exe").Replace("\", "/")
    $mtPath = (Join-Path $sdkBinX86 "mt.exe").Replace("\", "/")

    & $cmakePath -S $sourceDir -B $buildDir -G $resolvedGenerator -DTALOSFORGE_POLYHOOK_DIR="$polyHookDir" -DTALOSFORGE_USE_POLYHOOK=ON -DCMAKE_RC_COMPILER="$rcPath" -DCMAKE_MT="$mtPath" | Out-Host
}
else {
    & $cmakePath -S $sourceDir -B $buildDir -G $resolvedGenerator -A Win32 -DTALOSFORGE_POLYHOOK_DIR="$polyHookDir" -DTALOSFORGE_USE_POLYHOOK=ON | Out-Host
}

if ($LASTEXITCODE -ne 0) {
    throw "cmake configure failed with exit code $LASTEXITCODE."
}

& $cmakePath --build $buildDir --config $Configuration | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw "cmake build failed with exit code $LASTEXITCODE."
}

Write-Host "Native agent build completed."
Write-Host "Build dir: $buildDir"
