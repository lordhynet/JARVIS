$ErrorActionPreference = 'Stop'
$coreRoot = Split-Path $PSScriptRoot -Parent
$mobileRoot = Join-Path (Split-Path $coreRoot -Parent) 'JARVIS.MOBILE'
$actual = Join-Path $mobileRoot 'app/src/main/java/uk/jarvis/mobile/contracts/CoreContracts.kt'
$generated = Join-Path $env:TEMP ('jarvis-contracts-' + [guid]::NewGuid().ToString('N') + '.kt')
Push-Location $coreRoot
try {
    $workspaceSdk = Join-Path (Split-Path $coreRoot -Parent) '.tools/dotnet/dotnet.exe'
    $dotnet = if (Test-Path $workspaceSdk) { $workspaceSdk } else { 'dotnet' }
    & $dotnet run --project tools/Jarvis.ContractGen -- $generated
    if ($LASTEXITCODE -ne 0) { throw 'Contract generation failed.' }
    if ((Get-FileHash -LiteralPath $actual).Hash -ne (Get-FileHash -LiteralPath $generated).Hash) {
        throw 'Android contracts drifted. Regenerate CoreContracts.kt from Jarvis.Contracts.'
    }
    Write-Host 'Android contracts match the C# source.'
} finally { Pop-Location }
