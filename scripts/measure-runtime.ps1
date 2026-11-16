param()
$ErrorActionPreference = 'Stop'

$adminConnection = $env:JARVIS_PERF_TEST_POSTGRES
if ([string]::IsNullOrWhiteSpace($adminConnection)) {
    throw 'Set JARVIS_PERF_TEST_POSTGRES to a loopback PostgreSQL admin connection string targeting database=postgres.'
}
$connectionParts = @{}
foreach ($part in $adminConnection.Split(';')) {
    $separator = $part.IndexOf('=')
    if ($separator -gt 0) { $connectionParts[$part.Substring(0, $separator).Trim().ToLowerInvariant()] = $part.Substring($separator + 1).Trim().Trim('"', "'") }
}
$hostName = [string]($connectionParts['host'] ?? $connectionParts['server'])
$databaseName = [string]($connectionParts['database'] ?? $connectionParts['initial catalog'])
$loopback = $hostName -eq 'localhost'
if (!$loopback) {
    $address = $null
    $loopback = [System.Net.IPAddress]::TryParse($hostName, [ref]$address) -and [System.Net.IPAddress]::IsLoopback($address)
}
if (!$loopback -or $databaseName -ne 'postgres') {
    throw 'Refusing to run: only a loopback PostgreSQL connection targeting database=postgres is allowed. The test creates and drops its own randomly named database.'
}

$root = Split-Path $PSScriptRoot -Parent
$outputPath = Join-Path ([System.IO.Path]::GetTempPath()) "jarvis-runtime-measurement-$PID.json"
$previousOutput = $env:JARVIS_PERF_OUTPUT
$env:JARVIS_PERF_OUTPUT = $outputPath
Write-Output "Performance harness environment set: connection=$([bool]$env:JARVIS_PERF_TEST_POSTGRES), reportPath=$([bool]$env:JARVIS_PERF_OUTPUT)"
Push-Location $root
try {
    $dotnet = Join-Path (Split-Path $root -Parent) '.tools/dotnet/dotnet.exe'
    if (!(Test-Path -LiteralPath $dotnet)) { $dotnet = 'dotnet' }
    & $dotnet test tests/Jarvis.Tests/Jarvis.Tests.csproj --no-restore `
        --filter 'FullyQualifiedName~RuntimeMeasurementHarnessTests.Generate_synthetic_dataset_and_measure_runtime_hot_paths' `
        --logger 'console;verbosity=normal'
    if ($LASTEXITCODE -ne 0) { throw 'Runtime measurement harness failed.' }
    if (!(Test-Path -LiteralPath $outputPath)) { throw 'The measurement test did not write its report.' }
    Write-Output "Measurement report: $outputPath"
    Get-Content -LiteralPath $outputPath -Raw
} finally {
    $env:JARVIS_PERF_OUTPUT = $previousOutput
    Pop-Location
}
