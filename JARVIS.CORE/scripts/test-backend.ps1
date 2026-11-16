param([string]$Filter = '', [switch]$NoBuild, [switch]$Diagnostics, [ValidateRange(1, 16)][int]$MaxParallelThreads = 2)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'backend-configuration.ps1')
$configuration = Get-JarvisConfiguration
$connection = [System.Data.Common.DbConnectionStringBuilder]::new()
$connection['Host']='127.0.0.1'; $connection['Port']=$configuration.Database.ExposedPort
$connection['Database']='postgres'; $connection['Username']=$configuration.Database.Username; $connection['Password']=$configuration.Database.Password
$connection['GSS Encryption Mode']='Disable'
$previous = $env:JARVIS_TEST_POSTGRES
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    $env:JARVIS_TEST_POSTGRES = $connection.ConnectionString
    $arguments = @('test','Jarvis.sln','--no-restore','--verbosity','minimal','--logger','trx;LogFileName=intelligence.trx')
    if ($Filter) { $arguments += @('--filter', $Filter) }
    if ($NoBuild) { $arguments += '--no-build' }
    if ($Diagnostics) {
        $arguments += @('--logger', 'console;verbosity=normal', '--blame-hang-timeout', '5m', '--blame-hang-dump-type', 'none')
    }
    $arguments += @('--', "xUnit.MaxParallelThreads=$MaxParallelThreads")
    $workspaceSdk = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) '.tools/dotnet/dotnet.exe'
    $dotnet = if (Test-Path $workspaceSdk) { $workspaceSdk } else { 'dotnet' }
    & $dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Backend tests failed.' }
} finally { $env:JARVIS_TEST_POSTGRES = $previous; Pop-Location }
