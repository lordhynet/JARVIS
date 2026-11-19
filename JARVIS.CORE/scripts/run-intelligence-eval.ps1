param(
    [Parameter(Mandatory)][uri]$CoreBaseUrl,
    [string]$OutputPath = 'intelligence-evaluation.json',
    [guid]$RequestId = [guid]::NewGuid(),
    [switch]$RunPaid,
    [switch]$ConfirmPaid
)
$ErrorActionPreference = 'Stop'
if ($CoreBaseUrl.Scheme -ne 'https' -or $CoreBaseUrl.UserInfo) { throw 'Use a trusted HTTPS Core URL without embedded credentials.' }
if (!$env:JARVIS_CORE_KEY -or !$env:JARVIS_OWNER_KEY) { throw 'Set JARVIS_CORE_KEY and the separate JARVIS_OWNER_KEY in this process. Keys are never printed.' }
if ($RunPaid -and !$ConfirmPaid) { throw 'A paid run requires both -RunPaid and -ConfirmPaid, plus Core Intelligence:EvaluationsEnabled. It can incur AI charges.' }
$body = @{ requestId = $RequestId; runPaid = [bool]$RunPaid; allowCloudProcessing = [bool]($RunPaid -and $ConfirmPaid) } | ConvertTo-Json -Compress
$headers = @{ 'X-Jarvis-Key' = $env:JARVIS_CORE_KEY; 'X-Jarvis-Owner-Key' = $env:JARVIS_OWNER_KEY }
$result = Invoke-RestMethod -Uri ([uri]::new($CoreBaseUrl, '/api/intelligence/evaluations')) -Method Post -Headers $headers -ContentType 'application/json' -Body $body -TimeoutSec 900
$result | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Host ('Mode: ' + $result.mode + '; passed: ' + $result.passed + '; failed: ' + $result.failed + '; report: ' + $OutputPath)
if ($result.failed -gt 0) { exit 1 }
