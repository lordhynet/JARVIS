param(
    [string]$PostgresBin = 'C:/Program Files/PostgreSQL/18/bin',
    [int]$Port = 15440,
    [switch]$Browser
)
$ErrorActionPreference = 'Stop'
$cluster = Join-Path $env:TEMP ('jarvis-scenarios-' + [guid]::NewGuid().ToString('N'))
$oldTestConnection = $env:JARVIS_TEST_POSTGRES
$started = $false
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    if (!(Test-Path -LiteralPath (Join-Path $PostgresBin 'initdb.exe'))) { throw 'Pass -PostgresBin pointing to a PostgreSQL bin directory, or use run-milestone2.ps1 -Test for Docker.' }
    & (Join-Path $PostgresBin 'initdb.exe') -D $cluster -U jarvis_test -A trust --no-locale --encoding=UTF8
    if ($LASTEXITCODE -ne 0) { throw 'Could not initialize the isolated test database.' }
    & (Join-Path $PostgresBin 'pg_ctl.exe') -D $cluster -l (Join-Path $cluster 'server.log') -o "-p $Port -h 127.0.0.1" -w start
    if ($LASTEXITCODE -ne 0) { throw 'Could not start the test database. Try a different -Port.' }
    $started = $true
    $env:JARVIS_TEST_POSTGRES = "Host=127.0.0.1;Port=$Port;Database=postgres;Username=jarvis_test"
    dotnet test --logger 'trx;LogFileName=all-scenarios.trx'
    if ($LASTEXITCODE -ne 0) { throw 'Scenario tests failed.' }
    if ($Browser) {
        npm ci --no-fund
        if ($LASTEXITCODE -ne 0) { throw 'Browser dependency installation failed.' }
        npx playwright install chromium
        if ($LASTEXITCODE -ne 0) { throw 'Browser installation failed.' }
        npm run test:browser
        if ($LASTEXITCODE -ne 0) { throw 'Browser scenarios failed.' }
    }
} finally {
    $env:JARVIS_TEST_POSTGRES = $oldTestConnection
    if ($started) { & (Join-Path $PostgresBin 'pg_ctl.exe') -D $cluster -m fast -w stop }
    Write-Host "Isolated test cluster and logs retained at $cluster"
    Pop-Location
}
