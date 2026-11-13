param([switch]$Test, [ValidateRange(1024, 65535)][int]$DatabasePort = 15432)
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    # Keep database credentials out of Git. Never replace an existing password.
    if (!(Test-Path -LiteralPath '.env')) {
        $password = [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')
        Set-Content -LiteralPath '.env' -Value "JARVIS_POSTGRES_PASSWORD=$password"
    }
    $passwordLine = Get-Content -LiteralPath '.env' | Where-Object { $_ -match '^JARVIS_POSTGRES_PASSWORD=' } | Select-Object -First 1
    if (!$passwordLine) { throw '.env must contain JARVIS_POSTGRES_PASSWORD=<password>.' }
    $password = $passwordLine.Substring('JARVIS_POSTGRES_PASSWORD='.Length).Trim()
    # Generated hex passwords also avoid dotenv and connection-string quoting ambiguity.
    if ($password -notmatch '^[a-zA-Z0-9_-]{16,}$') {
        throw 'Use at least 16 letters, digits, underscores or hyphens for JARVIS_POSTGRES_PASSWORD.'
    }
    $oldPassword = $env:JARVIS_POSTGRES_PASSWORD
    $oldPort = $env:JARVIS_POSTGRES_PORT
    $oldConnection = $env:ConnectionStrings__Jarvis
    $oldProvider = $env:Jarvis__StorageProvider
    $oldTestConnection = $env:JARVIS_TEST_POSTGRES
    try {
        $env:JARVIS_POSTGRES_PASSWORD = $password
        $env:JARVIS_POSTGRES_PORT = "$DatabasePort"
        docker compose up -d --wait
        if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL did not start. Check Docker Desktop is running.' }
        $connection = "Host=127.0.0.1;Port=$DatabasePort;Database=jarvis;Username=jarvis;Password=$password"
        if ($Test) {
            $env:JARVIS_TEST_POSTGRES = $connection
            dotnet test
        } else {
            $env:ConnectionStrings__Jarvis = $connection
            $env:Jarvis__StorageProvider = 'Postgres'
            dotnet run --project src/Jarvis.Api
        }
        if ($LASTEXITCODE -ne 0) { throw 'The JARVIS command failed; see the output above.' }
    } finally {
        $env:JARVIS_POSTGRES_PASSWORD = $oldPassword
        $env:JARVIS_POSTGRES_PORT = $oldPort
        $env:ConnectionStrings__Jarvis = $oldConnection
        $env:Jarvis__StorageProvider = $oldProvider
        $env:JARVIS_TEST_POSTGRES = $oldTestConnection
    }
} finally {
    Pop-Location
}
