param(
    [Parameter(Mandatory = $true)][string]$BackupPath,
    [Parameter(Mandatory = $true)][string]$NewDatabaseName,
    [string]$ComposeFile = 'compose.yaml',
    [string]$ProjectName = ''
)
$ErrorActionPreference = 'Stop'
if ($NewDatabaseName -notmatch '^jarvis_restore_[a-z0-9_]{1,40}$') { throw 'Use a new database name beginning jarvis_restore_ followed by lowercase letters, digits or underscores.' }
$resolvedBackup = (Resolve-Path -LiteralPath $BackupPath).Path
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    $composeArgs = @('compose', '-f', $ComposeFile)
    if ($ProjectName) { $composeArgs += @('-p', $ProjectName) }
    $containerFile = '/tmp/restore-' + [guid]::NewGuid().ToString('N') + '.dump'
    & docker @composeArgs cp $resolvedBackup "postgres:$containerFile"
    if ($LASTEXITCODE -ne 0) { throw 'Could not copy backup into the database container.' }
    try {
        # createdb fails if this name already exists. Existing databases are never overwritten.
        & docker @composeArgs exec -T postgres createdb -U jarvis $NewDatabaseName
        if ($LASTEXITCODE -ne 0) { throw 'Could not create a new restore database. Choose an unused name.' }
        & docker @composeArgs exec -T postgres pg_restore -U jarvis -d $NewDatabaseName --no-owner --exit-on-error --single-transaction $containerFile
        if ($LASTEXITCODE -ne 0) { throw 'Restore failed. The original database is unchanged.' }
        Write-Host "Restored to $NewDatabaseName. Verify its data before changing the API connection string."
    } finally {
        & docker @composeArgs exec -T postgres rm -- $containerFile
    }
} finally { Pop-Location }
