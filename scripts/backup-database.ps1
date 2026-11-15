param(
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '../backups'),
    [string]$ComposeFile = 'compose.yaml',
    [string]$ProjectName = '',
    [string]$ConfigurationPath = ''
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'backend-configuration.ps1')
. (Join-Path $PSScriptRoot 'backup-protection.ps1')
$configuration = Get-JarvisConfiguration -Path $ConfigurationPath
$previousEnvironment = Set-JarvisComposeEnvironment $configuration
$plainTarget = $null
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    $composeArgs = @('compose', '-f', $ComposeFile)
    if ($ProjectName) { $composeArgs += @('-p', $ProjectName) }
    $directory = [IO.Path]::GetFullPath($OutputDirectory)
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    $name = 'jarvis-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.dump'
    $target = Join-Path $directory ($name + '.dpapi')
    $plainTarget = [IO.Path]::GetTempFileName()
    $containerFile = '/tmp/' + $name
    & docker @composeArgs exec -T postgres pg_dump -U $configuration.Database.Username -d $configuration.Database.Name --format=custom --file=$containerFile
    if ($LASTEXITCODE -ne 0) { throw 'Database backup failed.' }
    & docker @composeArgs cp "postgres:$containerFile" $plainTarget
    if ($LASTEXITCODE -ne 0) { throw 'Could not copy the backup out of the container.' }
    & docker @composeArgs exec -T postgres rm -- $containerFile
    if ($LASTEXITCODE -ne 0) { Write-Warning 'Backup copied, but the temporary container file could not be removed.' }
    Protect-JarvisBackup -Source $plainTarget -Target $target
    Get-FileHash -LiteralPath $target -Algorithm SHA256 | Format-List
    Write-Host "Backup saved: $target"
} finally { if ($plainTarget -and (Test-Path -LiteralPath $plainTarget)) { Remove-Item -LiteralPath $plainTarget -Force }; Restore-JarvisComposeEnvironment $previousEnvironment; Pop-Location }
