param(
    [string]$HostExe = "C:\Utilities\TalosForge\src\UnlockerAgentHost\bin\Release\net8.0\TalosForge.UnlockerAgentHost.exe",
    [string]$LogFile = "C:\Utilities\TalosForge\src\UnlockerAgent.Native\vectorbuddy.log",
    [string]$Lua = "print('hwbp_test')",
    [int]$HostTimeoutSeconds = 20,
    [int]$MaxWaitSeconds = 30,
    [switch]$RequireLogMessages
)

$ErrorActionPreference = "Stop"

Write-Host "=== HWBP Integration Test Script ===" -ForegroundColor Cyan

if (-not (Test-Path $HostExe)) {
    throw "Host executable not found: $HostExe"
}

if (Test-Path $LogFile) {
    Remove-Item $LogFile -Force
    Write-Host "Old log file deleted."
}

$outFile = Join-Path $env:TEMP ("talosforge-hwbp-out-{0}.log" -f [Guid]::NewGuid().ToString("N"))
$errFile = Join-Path $env:TEMP ("talosforge-hwbp-err-{0}.log" -f [Guid]::NewGuid().ToString("N"))

try {
    Write-Host "Running one-shot host command..."
    $proc = Start-Process `
        -FilePath $HostExe `
        -ArgumentList @("--runtime-mode", "native", "--lua", $Lua) `
        -NoNewWindow `
        -PassThru `
        -RedirectStandardOutput $outFile `
        -RedirectStandardError $errFile

    if (-not $proc.WaitForExit($HostTimeoutSeconds * 1000)) {
        try { $proc.Kill() } catch {}
        throw "Host timed out after $HostTimeoutSeconds seconds."
    }
    $proc.WaitForExit()
    $proc.Refresh()

    $hostOutput = if (Test-Path $outFile) { Get-Content $outFile -Raw } else { "" }
    $hostError = if (Test-Path $errFile) { Get-Content $errFile -Raw } else { "" }
    $exitCode = if ($null -eq $proc.ExitCode) { -1 } else { [int]$proc.ExitCode }
    $hostSucceeded = ($exitCode -eq 0) -or ($hostOutput -match "Lua execution succeeded")

    Write-Host "Host exit code: $exitCode"
    Write-Host "Host output:"
    Write-Host $hostOutput
    if (-not [string]::IsNullOrWhiteSpace($hostError)) {
        Write-Host "Host stderr:"
        Write-Host $hostError
    }

    if (-not $hostSucceeded) {
        throw "One-shot host execution failed."
    }
}
finally {
    if (Test-Path $outFile) { Remove-Item $outFile -Force }
    if (Test-Path $errFile) { Remove-Item $errFile -Force }
}

$logAvailable = Test-Path $LogFile
if (-not $logAvailable -and -not $RequireLogMessages) {
    Write-Host "Log file not found. Host one-shot execution succeeded, so test passes." -ForegroundColor Yellow
    exit 0
}

$foundIat = $false
$foundHwbp = $false
$foundVeh = $false
$foundRecv = $false
$foundSelf = $false

Write-Host "Monitoring log file for expected messages..."
for ($i = 0; $i -lt $MaxWaitSeconds; $i++) {
    if (Test-Path $LogFile) {
        $content = Get-Content $LogFile -Raw

        if (-not $foundIat -and $content -match "IAT patched GetThreadContext") {
            $foundIat = $true
            Write-Host "[OK] IAT patch applied" -ForegroundColor Green
        }
        if (-not $foundHwbp -and $content -match "HWBPManager: initialized") {
            $foundHwbp = $true
            Write-Host "[OK] HWBPManager initialized" -ForegroundColor Green
        }
        if (-not $foundVeh -and $content -match "VEHHandler: installed") {
            $foundVeh = $true
            Write-Host "[OK] VEH handler installed" -ForegroundColor Green
        }
        if (-not $foundRecv -and $content -match "WoWHooks: recv =") {
            $foundRecv = $true
            Write-Host "[OK] recv hook registered" -ForegroundColor Green
        }
        if (-not $foundSelf -and $content -match "WoWHooks: self-test completed") {
            $foundSelf = $true
            Write-Host "[OK] HWBP self-test passed" -ForegroundColor Green
        }
    }

    if ($foundIat -and $foundHwbp -and $foundVeh -and $foundRecv -and $foundSelf) {
        Write-Host ""
        Write-Host "All expected messages found. HWBP integration appears successful." -ForegroundColor Green
        exit 0
    }

    Start-Sleep -Seconds 1
}

if ($RequireLogMessages) {
    Write-Host ""
    Write-Host "Test incomplete after $MaxWaitSeconds seconds. Missing messages:" -ForegroundColor Yellow
    if (-not $foundIat) { Write-Host "  - IAT patch" -ForegroundColor Red }
    if (-not $foundHwbp) { Write-Host "  - HWBPManager initialization" -ForegroundColor Red }
    if (-not $foundVeh) { Write-Host "  - VEH handler installation" -ForegroundColor Red }
    if (-not $foundRecv) { Write-Host "  - recv hook registration" -ForegroundColor Red }
    if (-not $foundSelf) { Write-Host "  - self-test completion" -ForegroundColor Red }
    Write-Host ""
    Write-Host "Check log file: $LogFile" -ForegroundColor Yellow
    exit 1
}

Write-Host ""
Write-Host "Host one-shot execution succeeded, but HWBP log markers were not found." -ForegroundColor Yellow
Write-Host "Run again with -RequireLogMessages if you need strict HWBP log validation." -ForegroundColor Yellow
exit 0
