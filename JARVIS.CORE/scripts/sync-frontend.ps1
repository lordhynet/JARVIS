param([switch]$Check)
$ErrorActionPreference = 'Stop'
$coreRoot = Split-Path $PSScriptRoot -Parent
foreach ($file in @('modules.js', 'live.js', 'voice-capture.js', 'intelligence.js', 'privacy.js', 'planning.js', 'imports.js', 'notifications.js', 'travel.js', 'forge.js', 'recordings.js', 'sw.js')) {
$source = Join-Path (Split-Path $coreRoot -Parent) ('JARVIS.FRONTEND/' + $file)
$target = Join-Path $coreRoot ('src/Jarvis.Api/wwwroot/' + $file)
if ($Check) {
    if (!(Test-Path -LiteralPath $target) -or (Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $target).Hash) { throw 'Frontend module bundle drifted. Run scripts/sync-frontend.ps1.' }
    Write-Host 'Frontend module bundle matches its source.'
} else { Copy-Item -LiteralPath $source -Destination $target -Force; Write-Host 'Frontend modules packaged for Core hosting.' }
}
