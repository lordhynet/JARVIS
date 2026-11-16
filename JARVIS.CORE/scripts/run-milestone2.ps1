param([switch]$Test)
$ErrorActionPreference = 'Stop'
if ($Test) { & (Join-Path $PSScriptRoot 'test-backend.ps1') }
& (Join-Path $PSScriptRoot 'start-server.ps1')
