$ErrorActionPreference = 'Stop'
$p = Get-Process -Name Wow -ErrorAction SilentlyContinue
if (-not $p) { Write-Host "WoW not running"; exit 1 }

$wowPid = $p.Id
Write-Host "WoW PID: $wowPid"

$snap = [System.Diagnostics.Process]::GetProcessById($wowPid)
foreach ($mod in $snap.Modules) {
    if ($mod.ModuleName -match 'kernel32|KERNEL32') {
        Write-Host "Module: $($mod.ModuleName)"
        Write-Host "  Base:  0x$($mod.BaseAddress.ToString('X'))"
        Write-Host "  Path:  $($mod.FileName)"
        Write-Host "  Size:  $($mod.ModuleMemorySize)"
    }
}

# Check if the DLL file on disk has InitializeSListHead export
$k32path = "C:\Windows\SysWOW64\kernel32.dll"
if (Test-Path $k32path) {
    Write-Host "`nSysWOW64 kernel32.dll exists: $k32path"
    $bytes = [System.IO.File]::ReadAllBytes($k32path)
    Write-Host "  File size: $($bytes.Length)"
    $mz = [BitConverter]::ToUInt16($bytes, 0)
    Write-Host "  MZ sig: 0x$($mz.ToString('X4'))"
    $lfanew = [BitConverter]::ToInt32($bytes, 0x3C)
    Write-Host "  e_lfanew: 0x$($lfanew.ToString('X'))"
    $pe = [BitConverter]::ToUInt32($bytes, $lfanew)
    Write-Host "  PE sig: 0x$($pe.ToString('X8'))"
    $opt = $lfanew + 4 + 20
    $magic = [BitConverter]::ToUInt16($bytes, $opt)
    Write-Host "  Optional magic: 0x$($magic.ToString('X4'))"
    $expRva = [BitConverter]::ToUInt32($bytes, $opt + 96)
    $expSize = [BitConverter]::ToUInt32($bytes, $opt + 100)
    Write-Host "  Export dir RVA: 0x$($expRva.ToString('X')) size: $expSize"
} else {
    Write-Host "SysWOW64 kernel32.dll NOT FOUND"
}
