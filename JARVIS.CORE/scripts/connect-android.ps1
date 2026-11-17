param([Parameter(Mandatory)][string]$Device, [string]$Adb = (Join-Path $PSScriptRoot '../../.tools/android-sdk/platform-tools/adb.exe'))
$ErrorActionPreference = 'Stop'
Push-Location (Split-Path $PSScriptRoot -Parent)
try {
    $info = Get-Content -LiteralPath .https/server-info.json -Raw | ConvertFrom-Json
    . (Join-Path $PSScriptRoot 'backend-configuration.ps1')
    $configuration = Get-JarvisConfiguration
    $key = $configuration.Jarvis.ApiKey
    if (!$key) { throw 'Core connection credentials are missing in appsettings.json.' }
    $json = @{url=$configuration.Server.AndroidBridgeUrl;token=$key;certificateSha256=$info.certificateSha256} | ConvertTo-Json -Compress
    $payload = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($json)).TrimEnd('=').Replace('+','-').Replace('/','_')
    $phonePort = ([uri]$configuration.Server.AndroidBridgeUrl).Port
    $serverPort = [int]$configuration.Server.HttpsPort
    & $Adb -s $Device reverse "tcp:$phonePort" "tcp:$serverPort"
    if ($LASTEXITCODE -ne 0) { throw 'Could not create the private debugging tunnel.' }
    # Explicit package delivery over the already authenticated ADB channel. Do not print payload.
    & $Adb -s $Device shell am start -n uk.jarvis.mobile/.MainActivity --es jarvis.provision $payload | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not deliver pairing details to JARVIS.' }
    Write-Host 'Pairing details delivered. Tap Connect to my Core on the phone; no address or key entry is needed.'
} finally { Pop-Location }
