# JARVIS Milestone 1 smoke test.
# Start the API first (README step 4), then in a second terminal run:
#   .\scripts\smoke-test.ps1 -Key "<the key you set with user-secrets>"

param(
    [Parameter(Mandatory = $true)][string]$Key,
    [string]$BaseUrl = "http://localhost:5185"
)

$ErrorActionPreference = "Stop"
$headers = @{ "X-Jarvis-Key" = $Key }
$passed = 0; $failed = 0

function Check($name, [scriptblock]$test) {
    try {
        & $test
        Write-Host "PASS  $name" -ForegroundColor Green
        $script:passed++
    } catch {
        Write-Host "FAIL  $name -> $($_.Exception.Message)" -ForegroundColor Red
        $script:failed++
    }
}

function StatusOf([scriptblock]$call) {
    try { & $call | Out-Null; return 200 }
    catch { return [int]$_.Exception.Response.StatusCode }
}

Check "Health endpoint is public" {
    $h = Invoke-RestMethod "$BaseUrl/health"
    if ($h.status -ne "ok") { throw "status was $($h.status)" }
}

Check "Sessions reject a missing key" {
    $code = StatusOf { Invoke-RestMethod "$BaseUrl/api/sessions" }
    if ($code -ne 401) { throw "expected 401, got $code" }
}

$session = $null
Check "Create a mock-interview session" {
    $body = @{ activity = "MockInterview"; deviceId = "dell-laptop" } | ConvertTo-Json
    $script:session = Invoke-RestMethod "$BaseUrl/api/sessions" -Method Post -Headers $headers -Body $body -ContentType "application/json"
    if ($script:session.title -ne "Mock interview") { throw "unexpected title $($script:session.title)" }
}

Check "Read the session back" {
    $s = Invoke-RestMethod "$BaseUrl/api/sessions/$($session.id)" -Headers $headers
    if ($s.id -ne $session.id) { throw "id mismatch" }
}

Check "Add two checkpoints in order" {
    $b1 = @{ summary = "Asked about Kafka vs RabbitMQ"; currentTask = "Q2: event-driven design" } | ConvertTo-Json
    $b2 = @{ summary = "Discussed idempotent consumers" } | ConvertTo-Json
    Invoke-RestMethod "$BaseUrl/api/sessions/$($session.id)/checkpoints" -Method Post -Headers $headers -Body $b1 -ContentType "application/json" | Out-Null
    $s = Invoke-RestMethod "$BaseUrl/api/sessions/$($session.id)/checkpoints" -Method Post -Headers $headers -Body $b2 -ContentType "application/json"
    if ($s.checkpoints.Count -ne 2 -or $s.checkpoints[1].sequence -ne 2) { throw "checkpoints not in order" }
    if ($s.currentTask -ne "Q2: event-driven design") { throw "currentTask not kept" }
}

Check "Empty checkpoint is rejected" {
    $body = @{ summary = " " } | ConvertTo-Json
    $code = StatusOf { Invoke-RestMethod "$BaseUrl/api/sessions/$($session.id)/checkpoints" -Method Post -Headers $headers -Body $body -ContentType "application/json" }
    if ($code -ne 400) { throw "expected 400, got $code" }
}

Check "Unknown session returns 404" {
    $code = StatusOf { Invoke-RestMethod "$BaseUrl/api/sessions/$([guid]::NewGuid())" -Headers $headers }
    if ($code -ne 404) { throw "expected 404, got $code" }
}

Write-Host ""
Write-Host "$passed passed, $failed failed" -ForegroundColor ($(if ($failed -eq 0) { "Green" } else { "Red" }))
if ($failed -gt 0) { exit 1 }
