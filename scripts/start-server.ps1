param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'backend-configuration.ps1')
$configuration = Get-JarvisConfiguration
$previous = Set-JarvisComposeEnvironment $configuration
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    $arguments = @('compose', '-f', 'compose.yaml', '-f', 'compose.app.yaml')
    if ($configuration.Server.Tls.Enabled) { $arguments += @('-f', 'compose.mobile.yaml') }
    $arguments += @('up', '-d', '--wait')
    if (!$NoBuild) { $arguments += '--build' }
    & docker @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Core deployment failed. appsettings.json remains the configuration source.' }
} finally { Restore-JarvisComposeEnvironment $previous; Pop-Location }
