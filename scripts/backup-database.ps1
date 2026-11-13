param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../backups'),
    [string]$ComposeFile = 'compose.yaml',
    [string]$ProjectName = ''
)
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    $composeArgs = @('compose', '-f', $ComposeFile)
    if ($ProjectName) { $composeArgs += @('-p', $ProjectName) }
    $directory = [IO.Path]::GetFullPath($OutputDirectory)
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $name = 'jarvis-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.dump'
    $target = Join-Path $directory $name
    $containerFile = '/tmp/' + $name
    & docker @composeArgs exec -T postgres pg_dump -U jarvis -d jarvis --format=custom --file=$containerFile
    if ($LASTEXITCODE -ne 0) { throw 'Database backup failed.' }
    & docker @composeArgs cp "postgres:$containerFile" $target
    if ($LASTEXITCODE -ne 0) { throw 'Could not copy the backup out of the container.' }
    & docker @composeArgs exec -T postgres rm -- $containerFile
    if ($LASTEXITCODE -ne 0) { Write-Warning 'Backup copied, but the temporary container file could not be removed.' }
    Get-FileHash -LiteralPath $target -Algorithm SHA256 | Format-List
    Write-Host "Backup saved: $target"
} finally { Pop-Location }
