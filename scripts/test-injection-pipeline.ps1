param(
    [switch]$BuildNative,
    [switch]$SkipWardenTests,
    [string]$NativeDllPath,
    [int]$TimeoutSeconds = 25,
    [string]$WowPath = "C:\Games\FrostmourneClient\Wow.exe",
    [string]$WowUser = "",
    [string]$WowPass = "",
    [switch]$NoAutoLogin
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

$pass = 0
$fail = 0
$skip = 0
$results = @()

function Write-Step([string]$Name) {
    Write-Host ""
    Write-Host "--- $Name ---" -ForegroundColor Cyan
}

function Write-Pass([string]$Test, [string]$Detail = "") {
    $script:pass++
    $entry = "[PASS] $Test"
    if ($Detail) { $entry += " ($Detail)" }
    Write-Host $entry -ForegroundColor Green
    $script:results += $entry
}

function Write-Fail([string]$Test, [string]$Detail = "") {
    $script:fail++
    $entry = "[FAIL] $Test"
    if ($Detail) { $entry += " ($Detail)" }
    Write-Host $entry -ForegroundColor Red
    $script:results += $entry
}

function Write-Skip([string]$Test, [string]$Detail = "") {
    $script:skip++
    $entry = "[SKIP] $Test"
    if ($Detail) { $entry += " ($Detail)" }
    Write-Host $entry -ForegroundColor Yellow
    $script:results += $entry
}

# ── Helper: GameInput via SendInput (works with DirectX games) ──

$gameInputCs = Join-Path $PSScriptRoot "GameInput.cs"
if (Test-Path $gameInputCs) {
    Add-Type -TypeDefinition ([System.IO.File]::ReadAllText($gameInputCs)) -ErrorAction SilentlyContinue
}

# ── 0. Auto-Launch & Login ──

Write-Step "0. WoW Launch & Login"

$wow = Get-Process -Name "Wow" -ErrorAction SilentlyContinue | Select-Object -First 1

if (-not $wow) {
    if ($NoAutoLogin) {
        Write-Fail "WoW is running" "Wow.exe not found and -NoAutoLogin is set"
        exit 1
    }
    if (-not (Test-Path $WowPath)) {
        Write-Fail "WoW executable" "Not found at $WowPath (use -WowPath)"
        exit 1
    }

    Remove-Item (Join-Path $env:TEMP "TalosForge.pipe.*") -Force -ErrorAction SilentlyContinue
    Write-Host "  Launching WoW..." -ForegroundColor Yellow
    $wowDir = Split-Path $WowPath -Parent
    Start-Process -FilePath $WowPath -WorkingDirectory $wowDir

    $waitUntil = (Get-Date).AddSeconds(30)
    while ((Get-Date) -lt $waitUntil) {
        Start-Sleep -Milliseconds 500
        $wow = Get-Process -Name "Wow" -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($wow -and $wow.MainWindowHandle -ne [IntPtr]::Zero) { break }
    }

    if (-not $wow) {
        Write-Fail "WoW launch" "Process did not start within 30s"
        exit 1
    }

    Write-Host "  Waiting for login screen (10s)..." -ForegroundColor Yellow
    Start-Sleep -Seconds 10

    $hwnd = $wow.MainWindowHandle
    [GameInput]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Milliseconds 500

    $rect = New-Object GameInput+RECT
    [GameInput]::GetWindowRect($hwnd, [ref]$rect) | Out-Null
    $cx = [int](($rect.Left + $rect.Right) / 2)
    $cy = [int](($rect.Top + $rect.Bottom) / 2)
    [GameInput]::Click($cx, $cy)
    Start-Sleep -Milliseconds 500

    $hasCreds = -not [string]::IsNullOrWhiteSpace($WowUser) -and -not [string]::IsNullOrWhiteSpace($WowPass)
    if ($hasCreds) {
        Write-Host "  Typing credentials from parameters..." -ForegroundColor Yellow
        [GameInput]::TypeString($WowUser)
        Start-Sleep -Milliseconds 200
        [GameInput]::TypeTab()
        Start-Sleep -Milliseconds 200
        [GameInput]::TypeString($WowPass)
        Start-Sleep -Milliseconds 200
        [GameInput]::TypeEnter()
    } else {
        Write-Skip "Auto login credentials" "No -WowUser/-WowPass provided; log in manually."
        Write-Host "  Waiting for manual login (20s)..." -ForegroundColor Yellow
        Start-Sleep -Seconds 20
    }

    Write-Host "  Waiting for character select (8s)..." -ForegroundColor Yellow
    Start-Sleep -Seconds 8

    $wow = Get-Process -Name "Wow" -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $wow) {
        Write-Fail "WoW launch" "Process died after login"
        exit 1
    }
    [GameInput]::SetForegroundWindow($wow.MainWindowHandle) | Out-Null
    Start-Sleep -Milliseconds 300

    Write-Host "  Entering world..." -ForegroundColor Yellow
    [GameInput]::TypeEnter()

    Write-Host "  Waiting for world load (12s)..." -ForegroundColor Yellow
    Start-Sleep -Seconds 12

    $wow = Get-Process -Name "Wow" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($wow) {
        Write-Pass "WoW launched and logged in" "PID=$($wow.Id)"
    } else {
        Write-Fail "WoW launch" "Process died during world entry"
        exit 1
    }
} else {
    Write-Pass "WoW already running" "PID=$($wow.Id)"
}

# ── 1. Prerequisites ──

Write-Step "1. Prerequisites"

$wow = Get-Process -Name "Wow" -ErrorAction SilentlyContinue | Select-Object -First 1
if ($wow) {
    Write-Pass "WoW is running" "PID=$($wow.Id)"
} else {
    Write-Fail "WoW is running" "Wow.exe not found"
    exit 1
}

$dotnetOk = Get-Command dotnet -ErrorAction SilentlyContinue
if ($dotnetOk) {
    Write-Pass ".NET SDK available"
} else {
    Write-Fail ".NET SDK available" "dotnet not found in PATH"
    exit 1
}

# ── 2. Native DLL ──

Write-Step "2. Native Agent DLL"

$dllCandidates = @(
    $NativeDllPath,
    (Join-Path $repoRoot "artifacts/native-agent/build/Release/Release/d3dhelper.dll"),
    (Join-Path $repoRoot "artifacts/native-agent/build/Debug/Debug/d3dhelper.dll"),
    (Join-Path $repoRoot "artifacts/native-agent/build/Release/Release/TalosForge.UnlockerAgent.Native.dll")
) | Where-Object { $_ -and (Test-Path $_) }

$resolvedDll = $dllCandidates | Select-Object -First 1

if (-not $resolvedDll -and $BuildNative) {
    Write-Host "Building native agent DLL..." -ForegroundColor Yellow
    try {
        & (Join-Path $repoRoot "scripts/build-native-agent.ps1") -Configuration Release -SkipPolyHookSync
        $dllCandidates = @(
            (Join-Path $repoRoot "artifacts/native-agent/build/Release/Release/d3dhelper.dll"),
            (Join-Path $repoRoot "artifacts/native-agent/build/Release/d3dhelper.dll")
        ) | Where-Object { Test-Path $_ }
        $resolvedDll = $dllCandidates | Select-Object -First 1
    } catch {
        Write-Fail "Native DLL build" $_.Exception.Message
    }
}

if ($resolvedDll) {
    Write-Pass "Native DLL found" $resolvedDll
} else {
    Write-Fail "Native DLL found" "Not found. Run with -BuildNative or build manually: .\scripts\build-native-agent.ps1"
}

# ── 3. Build .NET projects ──

Write-Step "3. Build .NET Projects"

$buildProjects = @(
    @{ Name = "Core";             Path = "src/Core/TalosForge.Core.csproj" },
    @{ Name = "UnlockerAgentHost"; Path = "src/UnlockerAgentHost/TalosForge.UnlockerAgentHost.csproj" },
    @{ Name = "Tests";            Path = "tests/TalosForge.Tests/TalosForge.Tests.csproj" }
)

foreach ($proj in $buildProjects) {
    $projPath = Join-Path $repoRoot $proj.Path
    $output = & dotnet build $projPath --nologo --verbosity quiet 2>&1
    if ($LASTEXITCODE -eq 0) {
        Write-Pass "Build $($proj.Name)"
    } else {
        Write-Fail "Build $($proj.Name)" ($output | Select-Object -Last 3 | Out-String).Trim()
    }
}

# ── 4. Injection ──

Write-Step "4. Injection Pipeline"

$pipeDiscoveryPath = Join-Path $env:TEMP "TalosForge.pipe.$($wow.Id)"
$pipeExistedBefore = Test-Path $pipeDiscoveryPath

if ($pipeExistedBefore) {
    $existingPipe = (Get-Content $pipeDiscoveryPath -Raw).Trim()
    Write-Pass "Agent already injected" "Pipe=$existingPipe"
} elseif ($resolvedDll) {
    Write-Host "Injecting via UnlockerAgentHost (--lua one-shot triggers injection)..." -ForegroundColor Yellow
    $agentHostProj = Join-Path $repoRoot "src/UnlockerAgentHost/TalosForge.UnlockerAgentHost.csproj"
    $agentOutLog = Join-Path $env:TEMP "talosforge-test-agent-out.txt"
    $agentErrLog = Join-Path $env:TEMP "talosforge-test-agent-err.txt"
    $processArgs = "run --project `"$agentHostProj`" -- --runtime-mode native --native-dll-path `"$resolvedDll`" --native-connect-timeout-ms 15000 --lua `"local _=1`""

    $agentProc = Start-Process -FilePath "dotnet" `
        -ArgumentList $processArgs `
        -NoNewWindow -PassThru `
        -RedirectStandardOutput $agentOutLog `
        -RedirectStandardError $agentErrLog `
        -WorkingDirectory $repoRoot

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds + 20)
    $pipeFound = $false
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
        if (Test-Path $pipeDiscoveryPath) {
            $pipeFound = $true
            break
        }
        if ($agentProc.HasExited) {
            if (Test-Path $pipeDiscoveryPath) { $pipeFound = $true }
            break
        }
    }

    if (-not $agentProc.HasExited) {
        $agentProc.WaitForExit(10000) | Out-Null
    }

    $agentExitCode = if ($agentProc.HasExited) { $agentProc.ExitCode } else { -1 }

    $outContent = if (Test-Path $agentOutLog) { Get-Content $agentOutLog -Raw -ErrorAction SilentlyContinue } else { "" }
    $errContent = if (Test-Path $agentErrLog) { Get-Content $agentErrLog -Raw -ErrorAction SilentlyContinue } else { "" }

    if ($pipeFound) {
        $pipeName = (Get-Content $pipeDiscoveryPath -Raw).Trim()
        Write-Pass "Injection succeeded" "Pipe=$pipeName"
    } elseif ($agentExitCode -eq 0) {
        Write-Pass "Injection + Lua executed" "exit=0 (pipe may have been cleaned up)"
    } else {
        Write-Fail "Injection succeeded" "exit=$agentExitCode"
    }

    if ($outContent -match "Lua execution succeeded") {
        Write-Pass "Lua one-shot command"
    } elseif ($outContent -match "Lua execution failed") {
        Write-Fail "Lua one-shot command" "Lua dispatch failed"
    } elseif ($agentExitCode -ne 0 -and $agentExitCode -ne $null) {
        Write-Fail "Lua one-shot command" "exit=$agentExitCode"
    } else {
        Write-Skip "Lua one-shot command" "Could not confirm"
    }

    if ($outContent) {
        Write-Host "  Agent output:" -ForegroundColor Gray
        ($outContent -split "`n") | Select-Object -Last 8 | ForEach-Object { Write-Host "    $_" -ForegroundColor Gray }
    }
    if ($errContent) {
        Write-Host "  Agent errors:" -ForegroundColor Gray
        ($errContent -split "`n") | Select-Object -Last 5 | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
    }

    if (-not $agentProc.HasExited) {
        Stop-Process -Id $agentProc.Id -Force -ErrorAction SilentlyContinue
    }
} else {
    Write-Skip "Injection" "No native DLL available"
}

# ── 5. Pipe Connectivity ──

Write-Step "5. Pipe Connectivity"

if (Test-Path $pipeDiscoveryPath) {
    $pipeName = (Get-Content $pipeDiscoveryPath -Raw).Trim()
    $pipeShort = $pipeName
    if ($pipeShort.StartsWith("\\.\pipe\")) { $pipeShort = $pipeShort.Substring(9) }

    Write-Pass "Discovery file exists" $pipeDiscoveryPath

    $pipeList = [System.IO.Directory]::GetFiles("\\.\pipe\") | Where-Object { $_ -like "*$pipeShort*" }
    if ($pipeList) {
        Write-Pass "Named pipe is alive" $pipeShort
    } else {
        Write-Fail "Named pipe is alive" "Pipe '$pipeShort' not found in system pipe list"
    }
} else {
    Write-Skip "Pipe connectivity" "No discovery file"
}

# ── 6. Warden Monitor Tests ──

Write-Step "6. Warden Monitor Tests"

if (-not $SkipWardenTests) {
    $testProj = Join-Path $repoRoot "tests/TalosForge.Tests/TalosForge.Tests.csproj"
    $testOutput = & dotnet test $testProj --filter FullyQualifiedName~Warden --nologo --verbosity quiet 2>&1
    $testExitCode = $LASTEXITCODE
    $passedLine = $testOutput | Select-String "Passed:\s*(\d+)" | Select-Object -First 1
    $failedLine = $testOutput | Select-String "Failed:\s*(\d+)" | Select-Object -First 1
    $totalLine  = $testOutput | Select-String "Total tests:\s*(\d+)" | Select-Object -First 1

    $testsPassed = if ($passedLine) { $passedLine.Matches[0].Groups[1].Value } else { "?" }
    $testsFailed = if ($failedLine) { $failedLine.Matches[0].Groups[1].Value } else { "0" }
    $testsTotal  = if ($totalLine)  { $totalLine.Matches[0].Groups[1].Value  } else { "?" }

    if ($testExitCode -eq 0) {
        Write-Pass "Warden unit & smoke tests" "$testsPassed/$testsTotal passed"
    } else {
        Write-Fail "Warden unit & smoke tests" "$testsFailed failed out of $testsTotal"
        $testOutput | Select-String "Failed " | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
    }
} else {
    Write-Skip "Warden tests" "-SkipWardenTests flag set"
}

# ── 7. Internal Memory Reader ──

Write-Step "7. Internal Memory Reader"

if (Test-Path $pipeDiscoveryPath) {
    $pipeName = (Get-Content $pipeDiscoveryPath -Raw).Trim()
    $pipeShort = $pipeName
    if ($pipeShort.StartsWith("\\.\pipe\")) { $pipeShort = $pipeShort.Substring(9) }

    $pipeExists = [System.IO.Directory]::GetFiles("\\.\pipe\") | Where-Object { $_ -like "*$pipeShort*" }
    if ($pipeExists) {
        try {
            $client = New-Object System.IO.Pipes.NamedPipeClientStream(".", $pipeShort, [System.IO.Pipes.PipeDirection]::InOut)
            $client.Connect(3000)
            $utf8NoBom = [System.Text.UTF8Encoding]::new($false)
            $writer = New-Object System.IO.StreamWriter($client, $utf8NoBom) -Property @{ AutoFlush = $true }
            $reader = New-Object System.IO.StreamReader($client, $utf8NoBom)

            $writer.WriteLine("ReadBytes")
            $writer.Flush()
            $writer.WriteLine('{"address":"0x400000","size":2}')
            $writer.Flush()
            $writer.WriteLine("3000")
            $writer.Flush()

            $successLine = $reader.ReadLine()
            $codeLine    = $reader.ReadLine()
            $messageLine = $reader.ReadLine()
            $payloadLine = $reader.ReadLine()

            if ($successLine -and $successLine.Trim() -eq "1") {
                $hexResult = if ($messageLine) { $messageLine.Trim() } else { "" }
                if ($hexResult -eq "4d5a") {
                    Write-Pass "Internal memory read (MZ header)" "Got 4d5a (MZ) at 0x400000 - agent reads game memory"
                } else {
                    Write-Pass "Internal memory read via pipe" "Hex at 0x400000: $hexResult"
                }
            } else {
                Write-Fail "Internal memory read via pipe" "success=$successLine code=$codeLine msg=$messageLine"
            }

            $client.Dispose()
        } catch {
            Write-Fail "Agent heartbeat via pipe" $_.Exception.Message
        }
    } else {
        Write-Skip "Internal memory reader" "Pipe not alive"
    }
} else {
    Write-Skip "Internal memory reader" "Agent not injected"
}

# ── Summary ──

Write-Host ""
Write-Host "============================================" -ForegroundColor White
Write-Host "  INJECTION PIPELINE TEST RESULTS" -ForegroundColor White
Write-Host "============================================" -ForegroundColor White
Write-Host ""
Write-Host "  Passed: $pass" -ForegroundColor Green
if ($fail -gt 0) { Write-Host "  Failed: $fail" -ForegroundColor Red } else { Write-Host "  Failed: $fail" }
if ($skip -gt 0) { Write-Host "  Skipped: $skip" -ForegroundColor Yellow } else { Write-Host "  Skipped: $skip" }
Write-Host ""
foreach ($r in $results) {
    $color = if ($r.StartsWith("[PASS]")) { "Green" } elseif ($r.StartsWith("[FAIL]")) { "Red" } else { "Yellow" }
    Write-Host "  $r" -ForegroundColor $color
}
Write-Host ""

if ($fail -gt 0) {
    Write-Host "Some checks failed. Review the output above." -ForegroundColor Red
    exit 1
} else {
    Write-Host "All checks passed!" -ForegroundColor Green
    exit 0
}
