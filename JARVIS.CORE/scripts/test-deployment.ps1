param([int]$Port = 5198, [switch]$LiveTransit)
$ErrorActionPreference = 'Stop'
$project = 'jarvis-verify-' + [guid]::NewGuid().ToString('N').Substring(0, 10)
$previous = @{}
foreach ($name in @('JARVIS_VERIFY_DB_PASSWORD','JARVIS_VERIFY_API_KEY','JARVIS_VERIFY_PORT','JARVIS_VERIFY_SETTINGS_PATH','COMPOSE_DISABLE_ENV_FILE')) { $previous[$name] = [Environment]::GetEnvironmentVariable($name) }
$env:JARVIS_VERIFY_DB_PASSWORD = [guid]::NewGuid().ToString('N')
$env:JARVIS_VERIFY_API_KEY = [guid]::NewGuid().ToString('N')
$env:JARVIS_VERIFY_PORT = "$Port"
$base = "http://127.0.0.1:$Port"
$headers = @{ 'X-Jarvis-Key' = $env:JARVIS_VERIFY_API_KEY }
$composeArgs = @('compose','-f','compose.verify.yaml','-p',$project)
$checks = [System.Collections.Generic.List[string]]::new()
$report = $null
Push-Location (Split-Path $PSScriptRoot -Parent)
$fixtureDirectory = Join-Path $env:TEMP $project
New-Item -ItemType Directory -Path $fixtureDirectory -Force | Out-Null
$fixturePath = Join-Path $fixtureDirectory 'appsettings.json'
$restoredFixturePath = Join-Path $fixtureDirectory 'restored-appsettings.json'
$fixture = Get-Content appsettings.example.json -Raw | ConvertFrom-Json
$fixture.Database.Host = 'postgres'; $fixture.Database.Port = 5432
$fixture.Database.Name = 'jarvis'; $fixture.Database.Username = 'jarvis'
$fixture.Database.Password = $env:JARVIS_VERIFY_DB_PASSWORD
$fixture.OpenAI.ApiKey = ''; $fixture.Anthropic.ApiKey = ''
$fixture.Jarvis.ApiKey = $env:JARVIS_VERIFY_API_KEY
$fixture.BudgetManagement.OwnerKey = [guid]::NewGuid().ToString('N') + [guid]::NewGuid().ToString('N')
$fixture.BudgetManagement.SubjectId = 'isolated-verification-owner'
$headers['X-Jarvis-Owner-Key'] = $fixture.BudgetManagement.OwnerKey
$fixture.Server.Tls.Enabled = $false
$fixture | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $fixturePath
$env:JARVIS_VERIFY_SETTINGS_PATH = $fixturePath
$env:COMPOSE_DISABLE_ENV_FILE = '1'
function Wait-Ready {
    for ($attempt=0; $attempt -lt 40; $attempt++) {
        try { Invoke-RestMethod "$base/health/ready" -TimeoutSec 3 | Out-Null; return } catch { Start-Sleep -Seconds 1 }
    }
    throw 'API did not become ready.'
}
function Post([string]$Path, $Body) {
    Invoke-RestMethod "$base/api$Path" -Headers $headers -Method Post -ContentType 'application/json' -Body ($Body | ConvertTo-Json -Depth 10) -TimeoutSec 30
}
function Get-LearningFingerprint([ValidateSet('jarvis','jarvis_restore_verification')][string]$Database) {
    $value = Get-Content -LiteralPath ./scripts/restore-learning-fingerprint.sql -Raw |
        & docker @composeArgs exec -T postgres psql -U jarvis -d $Database -tA -v ON_ERROR_STOP=1
    if ($LASTEXITCODE -ne 0) { throw 'Canonical learning fingerprint failed.' }
    $fingerprint = (@($value) -join '') | ConvertFrom-Json
    if ($fingerprint.Canonical -notmatch '^[0-9a-f]{64}$' -or $fingerprint.Learning -notmatch '^[0-9a-f]{64}$') { throw 'Invalid canonical fingerprints.' }
    return $fingerprint
}
try {
    & docker @composeArgs up -d --wait postgres
    if ($LASTEXITCODE -ne 0) { throw 'Isolated PostgreSQL start failed.' }
    & docker @composeArgs build api
    if ($LASTEXITCODE -ne 0) { throw 'Isolated API/migrator image build failed.' }
    & docker @composeArgs run --rm --no-deps api --migrate-only
    if ($LASTEXITCODE -ne 0) { throw 'Isolated explicit schema migration failed.' }
    & docker @composeArgs up -d --wait api
    if ($LASTEXITCODE -ne 0) { throw 'Container build/start failed.' }
    Wait-Ready
    $checks.Add('Container build and database readiness')
    $page = Invoke-WebRequest $base -TimeoutSec 10
    if ($page.Content -notmatch 'Personal companion') { throw 'Companion page is missing from the image.' }
    $checks.Add('Published companion page')
    $session = Post '/sessions' @{ activity='SystemDesign'; title='Deployment life scenario' }
    $saved = Post "/sessions/$($session.id)/checkpoints" @{ summary='Designed queue recovery'; currentTask='Retry policy' }
    $card = Post '/learning' @{ domain='SystemDesign'; prompt='Why idempotency?'; answer='Safe retries' }
    $reviewRequest = @{ requestId=[guid]::NewGuid().ToString(); rating='Good'; responseSeconds=1.23456789 }
    $reviewed = Post "/learning/$($card.id)/reviews" $reviewRequest
    $checks.Add('Persistent session, checkpoint and learning review')
    & docker @composeArgs restart api
    if ($LASTEXITCODE -ne 0) { throw 'API container restart failed.' }
    Wait-Ready
    $restored = Invoke-RestMethod "$base/api/sessions/$($session.id)" -Headers $headers -TimeoutSec 10
    if ($restored.currentTask -ne 'Retry policy' -or $restored.checkpoints.Count -ne 1) { throw 'Session did not survive container restart.' }
    $reviews = Invoke-RestMethod "$base/api/learning/$($card.id)/reviews" -Headers $headers -TimeoutSec 10
    if (@($reviews).Count -ne 1) { throw 'Learning review did not survive container restart.' }
    $checks.Add('Real API container restart survival')
    & docker @composeArgs stop api
    if ($LASTEXITCODE -ne 0) { throw 'Could not stop synthetic writers before backup.' }
    $checks.Add('Stopped synthetic API writer before fixture snapshot and backup')
    # Seed non-empty relational fixtures directly in this isolated database. The route is disabled:
    # restoring it must not trigger a live TfL request or need provider credentials.
    Get-Content -LiteralPath ./scripts/restore-fixture.sql -Raw | & docker @composeArgs exec -T postgres psql -U jarvis -d jarvis -v ON_ERROR_STOP=1
    if ($LASTEXITCODE -ne 0) { throw 'Could not seed relational restore fixtures.' }
    $expectedCounts = Get-Content -LiteralPath ./scripts/restore-counts.sql -Raw | & docker @composeArgs exec -T postgres psql -U jarvis -d jarvis -tA -v ON_ERROR_STOP=1
    if ($LASTEXITCODE -ne 0) { throw 'Could not snapshot relational restore fixtures.' }
    $expectedCounts = $expectedCounts | ConvertFrom-Json -AsHashtable
    if ($expectedCounts.AudioSingleton -ne 1 -or $expectedCounts.VoiceTurns -ne 1 -or $expectedCounts.TravelRoutes -ne 1 -or $expectedCounts.TravelLineStates -ne 1 -or $expectedCounts.TravelMonitorSingleton -ne 1) { throw 'Relational restore fixtures are incomplete.' }
    if ($expectedCounts.LearningCards -ne 1 -or $expectedCounts.LearningReviews -ne 1 -or $expectedCounts.LearningRows -ne 2 -or $expectedCounts.LearningControl -ne 1) { throw 'Shared learning fixtures are incomplete.' }
    $expectedFingerprint = Get-LearningFingerprint 'jarvis'
    $output = Join-Path $PWD "TestResults/$project"
    & ./scripts/backup-database.ps1 -OutputDirectory $output -ComposeFile compose.verify.yaml -ProjectName $project -ConfigurationPath $fixturePath
    $backup = Get-ChildItem -LiteralPath $output -Filter '*.dump.dpapi' | Select-Object -First 1
    if (!$backup) { throw 'Backup artifact missing.' }
    & ./scripts/restore-database.ps1 -BackupPath $backup.FullName -NewDatabaseName jarvis_restore_verification -ComposeFile compose.verify.yaml -ProjectName $project -ConfigurationPath $fixturePath
    $sessionCount = & docker @composeArgs exec -T postgres psql -U jarvis -d jarvis_restore_verification -tAc 'SELECT count(*) FROM "Sessions"'
    if ($LASTEXITCODE -ne 0 -or "$sessionCount".Trim() -ne '1') { throw 'Restored session count is incorrect.' }
    $checkpointCount = & docker @composeArgs exec -T postgres psql -U jarvis -d jarvis_restore_verification -tAc 'SELECT count(*) FROM "Checkpoints"'
    if ($LASTEXITCODE -ne 0 -or "$checkpointCount".Trim() -ne '1') { throw 'Restored checkpoint count is incorrect.' }
    $restoredFingerprint = Get-LearningFingerprint 'jarvis_restore_verification'
    if ($restoredFingerprint.Canonical -cne $expectedFingerprint.Canonical) { throw 'Canonical root/learning/control/history changed across restore.' }
    $restoredCounts = Get-Content -LiteralPath ./scripts/restore-counts.sql -Raw | & docker @composeArgs exec -T postgres psql -U jarvis -d jarvis_restore_verification -tA -v ON_ERROR_STOP=1
    if ($LASTEXITCODE -ne 0) { throw 'Could not read restored relational runtime/travel state.' }
    $restoredCounts = $restoredCounts | ConvertFrom-Json -AsHashtable
    foreach ($table in $expectedCounts.Keys) { if ($restoredCounts[$table] -ne $expectedCounts[$table]) { throw "Restored $table count differs from the fixture." } }
    $checks.Add('Restored hot runtime singleton, voice turns/tickets and travel route/line/monitor counts')
    $checks.Add('Current shared learning rows, global sequence and canonical fingerprint preserved')
    $checks.Add('Binary backup and restore into a separate database')
    # Only this generated verification configuration changes; source DB remains stopped.
    $fixture.Database.Name = 'jarvis_restore_verification'
    $fixture | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $restoredFixturePath
    $env:JARVIS_VERIFY_SETTINGS_PATH = $restoredFixturePath
    & docker @composeArgs run --rm --no-deps api --migrate-only
    if ($LASTEXITCODE -ne 0) { throw 'Restored database does not match the rehearsal binary.' }
    if ((Get-LearningFingerprint 'jarvis_restore_verification').Canonical -cne $expectedFingerprint.Canonical) { throw 'Matching migrator changed restored canonical state.' }
    & docker @composeArgs up -d --force-recreate --wait api
    if ($LASTEXITCODE -ne 0) { throw 'Restored synthetic API did not start.' }
    Wait-Ready
    $replayed = Post "/learning/$($card.id)/reviews" $reviewRequest
    if (($replayed | ConvertTo-Json -Depth 10 -Compress) -cne ($reviewed | ConvertTo-Json -Depth 10 -Compress)) { throw 'Original fractional review did not replay from the restored database.' }
    # Operational workers may change unrelated cold metadata after startup.
    if ((Get-LearningFingerprint 'jarvis_restore_verification').Learning -cne $expectedFingerprint.Learning) { throw 'Restored review replay changed canonical learning state.' }
    $checks.Add('Matching migration-only command, restored API startup and original fractional-review replay')
    if ($LiveTransit) {
        $transit = Invoke-RestMethod "$base/api/transit?lines=jubilee" -Headers $headers -TimeoutSec 50
        if ($transit[0].id -ne 'jubilee') { throw 'Live TfL response did not include Jubilee.' }
        $checks.Add('Live TfL through the deployed API')
    }
    # Destructive checks target only this randomly named verification project.
    $privacy = Invoke-RestMethod "$base/api/privacy/status" -Headers $headers -TimeoutSec 10
    $export = Invoke-RestMethod "$base/api/privacy/export" -Headers $headers -TimeoutSec 10
    if (@($export.sessions).Count -ne 1) { throw 'Privacy export missed the fixture session.' }
    $eraseRequest = @{ requestId=[guid]::NewGuid().ToString(); expectedRevision=$privacy.revision; confirmation='ERASE MY CORE DATA' }
    $erased = Post '/privacy/erase' $eraseRequest
    if (!$erased.erased) { throw 'Isolated Core was not marked erased.' }
    $retry = Post '/privacy/erase' $eraseRequest
    if ($retry.epoch -ne $erased.epoch) { throw 'Erasure retry changed the data generation.' }
    $empty = Invoke-RestMethod "$base/api/privacy/export" -Headers $headers -TimeoutSec 10
    if (@($empty.sessions).Count -ne 0 -or @($empty.learning).Count -ne 0) { throw 'Isolated erasure retained private fixture data.' }
    $blocked = Invoke-WebRequest "$base/api/sessions" -Headers $headers -SkipHttpErrorCheck -TimeoutSec 10
    if ($blocked.StatusCode -ne 503) { throw 'Erased Core still accepts ordinary operations.' }
    $checks.Add('Isolated export, atomic erasure, idempotent retry and maintenance gate')
    $report = [pscustomobject]@{ completedAt=[DateTimeOffset]::UtcNow.ToString('O'); project=$project;
        scope='Synthetic same-schema stopped-writer backup/restore/resume; not operational D1 or upgrade/Down proof';
        expectedCounts=$expectedCounts; restoredCounts=$restoredCounts; fingerprints=$expectedFingerprint;
        checks=$checks; result='ChecksPassed'; cleanup='Pending' }
} finally {
    # This random project owns only the test resources created above.
    $cleanupFailed = $false
    try {
        & docker @composeArgs down --volumes
        $cleanupFailed = $LASTEXITCODE -ne 0
        if (!$cleanupFailed) {
            foreach ($path in @($fixturePath,$restoredFixturePath)) { if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force } }
        }
        if ($null -ne $report) {
            $report.cleanup = if ($cleanupFailed) { 'Failed' } else { 'Passed' }
            $report.result = if ($cleanupFailed) { 'Failed' } else { 'Passed' }
            $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $output 'deployment-report.json')
            $report | ConvertTo-Json -Depth 5
        }
    } finally {
        foreach ($name in $previous.Keys) { [Environment]::SetEnvironmentVariable($name, $previous[$name]) }
        Pop-Location
    }
    if ($cleanupFailed) { throw 'Synthetic project cleanup failed; generated settings retained for recovery.' }
}
