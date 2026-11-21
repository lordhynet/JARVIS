param([switch]$NoBuild)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'backend-configuration.ps1')
$configuration = Get-JarvisConfiguration
$previous = Set-JarvisComposeEnvironment $configuration
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    $compose = @('compose', '-f', 'compose.yaml', '-f', 'compose.app.yaml')
    if ($configuration.Server.Tls.Enabled) { $compose += @('-f', 'compose.mobile.yaml') }
    $states = @(& docker @compose ps --all --format '{{.Service}}:{{.State}}')
    if ($LASTEXITCODE -ne 0) { throw 'Could not inspect Core containers; no migration attempted.' }
    if ($states | Where-Object { $_ -match '^api:' -and $_ -notmatch '^api:(exited|dead|created)$' }) {
        throw 'Stop Core writers and verify the backup before running the designated migrator.'
    }
    # Operator must also stop any writers outside this Compose project.
    & docker @compose up -d --wait postgres
    if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL did not become ready; no migration attempted.' }
    if (!$NoBuild) {
        & docker @compose build api
        if ($LASTEXITCODE -ne 0) { throw 'Migrator image build failed; no migration attempted.' }
    }
    & docker @compose run --rm --no-deps api --migrate-only
    if ($LASTEXITCODE -ne 0) { throw 'Schema migration refused or failed. Core has not been started.' }
} finally { Restore-JarvisComposeEnvironment $previous; Pop-Location }
