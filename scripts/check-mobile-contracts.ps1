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
    $checkedIn = [IO.File]::ReadAllText($actual).Replace("`r`n", "`n")
    $expected = [IO.File]::ReadAllText($generated)
    if ($checkedIn -cne $expected) {
        throw 'Android contracts or companion routes drifted. Regenerate CoreContracts.kt with Jarvis.ContractGen.'
    }
    Write-Host 'Android contracts and companion routes match the C# source.'
} finally {
    if (Test-Path -LiteralPath $generated) { Remove-Item -LiteralPath $generated }
    Pop-Location
}
