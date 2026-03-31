param([int]$MaxWaitSeconds = 60)
Write-Host "Waiting up to ${MaxWaitSeconds}s for WoW to start..."
$deadline = (Get-Date).AddSeconds($MaxWaitSeconds)
while ((Get-Date) -lt $deadline) {
    $wow = Get-Process -Name Wow -ErrorAction SilentlyContinue
    if ($wow) {
        Write-Host "WoW running! PID: $($wow.Id)"
        exit 0
    }
    Start-Sleep -Seconds 2
}
Write-Host "WoW not found within ${MaxWaitSeconds}s. Please start WoW manually."
exit 1
