param()

$ErrorActionPreference = 'Stop'

$required = @(
  'docs/README.md',
  'docs/architecture.md',
  'docs/ipc-contract.md'
)

foreach ($file in $required) {
  if (-not (Test-Path $file)) {
    throw "Missing required docs file: $file"
  }
}

$ipc = Get-Content -Raw -Path 'docs/ipc-contract.md'
$requiredIpcTokens = @('TalosForge.Cmd.v1', 'TalosForge.Evt.v1', 'LuaDoString', 'MoveTo')
foreach ($token in $requiredIpcTokens) {
  if ($ipc -notmatch [Regex]::Escape($token)) {
    throw "IPC doc missing token: $token"
  }
}

Write-Host 'Documentation checks passed.'
