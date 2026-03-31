Add-Type -MemberDefinition @'
[DllImport("kernel32.dll", SetLastError = true)]
public static extern bool IsWow64Process(IntPtr processHandle, out bool wow64Process);
'@ -Name K32 -Namespace TF

$p = Get-Process -Name Wow -ErrorAction Stop
$h = $p.Handle
$isWow64 = $false
[TF.K32]::IsWow64Process($h, [ref]$isWow64) | Out-Null
Write-Host "WoW PID: $($p.Id)"
Write-Host "IsWow64 (32-bit on 64-bit OS): $isWow64"
Write-Host "Host is 64-bit: $([Environment]::Is64BitProcess)"
Write-Host "OS is 64-bit: $([Environment]::Is64BitOperatingSystem)"
