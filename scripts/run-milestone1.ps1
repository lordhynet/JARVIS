# Compatibility entry point. Backend configuration lives only in root appsettings.json.
$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'start-server.ps1')
