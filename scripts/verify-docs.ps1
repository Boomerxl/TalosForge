param()

$ErrorActionPreference = 'Stop'

$required = @(
  'README.md',
  'docs/README.md',
  'docs/architecture.md',
  'docs/ipc-contract.md',
  'scripts/supervisor.ps1'
)

foreach ($file in $required) {
  if (-not (Test-Path $file)) {
    throw "Missing required docs file: $file"
  }
}

$checks = @(
  @{
    File = 'README.md'
    Tokens = @('scripts/supervisor.ps1', 'session_id')
  },
  @{
    File = 'docs/README.md'
    Tokens = @('scripts/supervisor.ps1', 'status -Json')
  },
  @{
    File = 'docs/architecture.md'
    Tokens = @('scripts/supervisor.ps1', 'session_id', 'MainWindow.xaml')
  },
  @{
    File = 'docs/ipc-contract.md'
    Tokens = @('TalosForge.Cmd.v1', 'TalosForge.Evt.v1', 'LuaDoString', 'MoveTo', 'session_id')
  }
)

foreach ($check in $checks) {
  $content = Get-Content -Raw -Path $check.File
  foreach ($token in $check.Tokens) {
    if ($content -notmatch [Regex]::Escape($token)) {
      throw "$($check.File) missing token: $token"
    }
  }
}

Write-Host 'Documentation checks passed.'
