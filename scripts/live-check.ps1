param(
    [string]$BaseUrl = '',
    [string]$Key = '',
    [switch]$Conversation
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'backend-configuration.ps1')
$configuration = Get-JarvisConfiguration
if (!$Key) { $Key = $configuration.Jarvis.ApiKey }
if (!$BaseUrl) { $BaseUrl = 'http://127.0.0.1:' + $configuration.Server.HttpPort }
$headers = @{ 'X-Jarvis-Key' = $Key }
$results = [System.Collections.Generic.List[object]]::new()
function Check([string]$Name, [scriptblock]$Action) {
    try {
        & $Action | Out-Null
        $results.Add([pscustomobject]@{ Check = $Name; Result = 'Passed'; Detail = '' })
    } catch {
        $results.Add([pscustomobject]@{ Check = $Name; Result = 'Failed'; Detail = $_.Exception.Message })
    }
}
Check 'Process health' { Invoke-RestMethod "$BaseUrl/health" -TimeoutSec 10 }
Check 'Database readiness' { Invoke-RestMethod "$BaseUrl/health/ready" -TimeoutSec 10 }
Check 'Live TfL' { Invoke-RestMethod "$BaseUrl/api/transit?lines=jubilee" -Headers $headers -TimeoutSec 50 }
$status = Invoke-RestMethod "$BaseUrl/api/integrations" -Headers $headers -TimeoutSec 10
$start = [uri]::EscapeDataString([DateTimeOffset]::UtcNow.ToString('O'))
$end = [uri]::EscapeDataString([DateTimeOffset]::UtcNow.AddDays(1).ToString('O'))
foreach ($provider in @('google', 'microsoft')) {
    $name = if ($provider -eq 'google') { 'GoogleCalendar' } else { 'MicrosoftCalendar' }
    if (($status | Where-Object name -eq $name).configured) {
        Check "Live $name" { Invoke-RestMethod "$BaseUrl/api/calendar/$provider`?start=$start&end=$end" -Headers $headers -TimeoutSec 50 }
    } else { $results.Add([pscustomobject]@{ Check = "Live $name"; Result = 'Not configured'; Detail = 'Supply server-side OAuth credentials.' }) }
}
if ($Conversation) {
    Check 'Live OpenAI conversation' {
        $session = Invoke-RestMethod "$BaseUrl/api/sessions" -Headers $headers -Method Post -ContentType 'application/json' -Body (@{activity='General'; title='Live connection test'} | ConvertTo-Json)
        try {
            Invoke-RestMethod "$BaseUrl/api/sessions/$($session.id)/messages" -Headers $headers -Method Post -ContentType 'application/json' -Body (@{requestId=[guid]::NewGuid().ToString(); text='Reply with: JARVIS connected.'} | ConvertTo-Json) -TimeoutSec 60
        } finally {
            Invoke-RestMethod "$BaseUrl/api/sessions/$($session.id)/status" -Headers $headers -Method Post -ContentType 'application/json' -Body '{"status":"Ended"}' | Out-Null
        }
    }
}
$results | Format-Table -Wrap
if ($results.Result -contains 'Failed') { throw 'One or more live checks failed.' }
Write-Host 'Physical home control and microphone/speaker checks must be performed from the companion on each actual device.'
