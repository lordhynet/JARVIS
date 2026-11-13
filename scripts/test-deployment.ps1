param([int]$Port = 5198, [switch]$LiveTransit)
$ErrorActionPreference = 'Stop'
$project = 'jarvis-verify-' + [guid]::NewGuid().ToString('N').Substring(0, 10)
$previous = @{}
foreach ($name in @('JARVIS_VERIFY_DB_PASSWORD','JARVIS_VERIFY_API_KEY','JARVIS_VERIFY_PORT')) { $previous[$name] = [Environment]::GetEnvironmentVariable($name) }
$env:JARVIS_VERIFY_DB_PASSWORD = [guid]::NewGuid().ToString('N')
$env:JARVIS_VERIFY_API_KEY = [guid]::NewGuid().ToString('N')
$env:JARVIS_VERIFY_PORT = "$Port"
$base = "http://127.0.0.1:$Port"
$headers = @{ 'X-Jarvis-Key' = $env:JARVIS_VERIFY_API_KEY }
$composeArgs = @('compose','-f','compose.verify.yaml','-p',$project)
$checks = [System.Collections.Generic.List[string]]::new()
Push-Location (Split-Path $PSScriptRoot -Parent)
function Wait-Ready {
    for ($attempt=0; $attempt -lt 40; $attempt++) {
        try { Invoke-RestMethod "$base/health/ready" -TimeoutSec 3 | Out-Null; return } catch { Start-Sleep -Seconds 1 }
    }
    throw 'API did not become ready.'
}
function Post([string]$Path, $Body) {
    Invoke-RestMethod "$base/api$Path" -Headers $headers -Method Post -ContentType 'application/json' -Body ($Body | ConvertTo-Json -Depth 10) -TimeoutSec 30
}
try {
    & docker @composeArgs up -d --build --wait
    if ($LASTEXITCODE -ne 0) { throw 'Container build/start failed.' }
    Wait-Ready
    $checks.Add('Container build and database readiness')
    $page = Invoke-WebRequest $base -TimeoutSec 10
    if ($page.Content -notmatch 'Personal companion') { throw 'Companion page is missing from the image.' }
    $checks.Add('Published companion page')
    $session = Post '/sessions' @{ activity='SystemDesign'; title='Deployment life scenario' }
    $saved = Post "/sessions/$($session.id)/checkpoints" @{ summary='Designed queue recovery'; currentTask='Retry policy' }
    $card = Post '/learning' @{ domain='SystemDesign'; prompt='Why idempotency?'; answer='Safe retries' }
    Post "/learning/$($card.id)/reviews" @{ requestId=[guid]::NewGuid().ToString(); rating='Good'; responseSeconds=2 } | Out-Null
    $checks.Add('Persistent session, checkpoint and learning review')
    & docker @composeArgs restart api
    if ($LASTEXITCODE -ne 0) { throw 'API container restart failed.' }
    Wait-Ready
    $restored = Invoke-RestMethod "$base/api/sessions/$($session.id)" -Headers $headers -TimeoutSec 10
    if ($restored.currentTask -ne 'Retry policy' -or $restored.checkpoints.Count -ne 1) { throw 'Session did not survive container restart.' }
    $reviews = Invoke-RestMethod "$base/api/learning/$($card.id)/reviews" -Headers $headers -TimeoutSec 10
    if (@($reviews).Count -ne 1) { throw 'Learning review did not survive container restart.' }
    $checks.Add('Real API container restart survival')
    $output = Join-Path $PWD "TestResults/$project"
    & ./scripts/backup-database.ps1 -OutputDirectory $output -ComposeFile compose.verify.yaml -ProjectName $project
    $backup = Get-ChildItem -LiteralPath $output -Filter '*.dump' | Select-Object -First 1
    if (!$backup) { throw 'Backup artifact missing.' }
    & ./scripts/restore-database.ps1 -BackupPath $backup.FullName -NewDatabaseName jarvis_restore_verification -ComposeFile compose.verify.yaml -ProjectName $project
    $sessionCount = & docker @composeArgs exec -T postgres psql -U jarvis -d jarvis_restore_verification -tAc 'SELECT count(*) FROM "Sessions"'
    if ($LASTEXITCODE -ne 0 -or "$sessionCount".Trim() -ne '1') { throw 'Restored session count is incorrect.' }
    $checkpointCount = & docker @composeArgs exec -T postgres psql -U jarvis -d jarvis_restore_verification -tAc 'SELECT count(*) FROM "Checkpoints"'
    if ($LASTEXITCODE -ne 0 -or "$checkpointCount".Trim() -ne '1') { throw 'Restored checkpoint count is incorrect.' }
    $runtimeJson = & docker @composeArgs exec -T postgres psql -U jarvis -d jarvis_restore_verification -tAc 'SELECT "Json" FROM "RuntimeDocuments" WHERE "Id"=1'
    if ($LASTEXITCODE -ne 0) { throw 'Could not read restored runtime state.' }
    $runtime = $runtimeJson | ConvertFrom-Json -AsHashtable
    if ($runtime.Learning.Count -ne 1 -or $runtime.Reviews.Count -ne 1) { throw 'Restored learning state is incorrect.' }
    $checks.Add('Binary backup and restore into a separate database')
    if ($LiveTransit) {
        $transit = Invoke-RestMethod "$base/api/transit?lines=jubilee" -Headers $headers -TimeoutSec 50
        if ($transit[0].id -ne 'jubilee') { throw 'Live TfL response did not include Jubilee.' }
        $checks.Add('Live TfL through the deployed API')
    }
    $report = [pscustomobject]@{ completedAt=[DateTimeOffset]::UtcNow.ToString('O'); checks=$checks; result='Passed' }
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'deployment-report.json')
    $report | ConvertTo-Json -Depth 5
} finally {
    # This random project owns only the test resources created above.
    & docker @composeArgs down --volumes
    foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name]) }
    Pop-Location
}
