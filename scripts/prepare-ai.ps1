$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'backend-configuration.ps1')
$configuration = Get-JarvisConfiguration
$configuration.PaidAI.Enabled = $true
if ($configuration.PaidAI.DailyLimitUsd -le 0) { $configuration.PaidAI.DailyLimitUsd = 2 }
if ($configuration.PaidAI.MonthlyLimitUsd -le 0) { $configuration.PaidAI.MonthlyLimitUsd = 50 }
if ($configuration.PaidAI.BackgroundLimitUsd -le 0) { $configuration.PaidAI.BackgroundLimitUsd = 15 }
$configuration | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path (Split-Path $PSScriptRoot -Parent) 'appsettings.json')
Write-Host 'AI defaults prepared in appsettings.json. Existing provider keys were preserved.'
