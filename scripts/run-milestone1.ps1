# Builds, tests, publishes and starts JARVIS Core, then runs the smoke test.
# Usage (from anywhere):
#   powershell -NoProfile -ExecutionPolicy Bypass -File "<repo>\scripts\run-milestone1.ps1"
# Results are written to <repo>\logs\. The API key is never written to the logs.

$ErrorActionPreference = "Continue"
$repo    = Split-Path -Parent $PSScriptRoot
$logs    = Join-Path $repo "logs"
$publish = Join-Path $repo "publish"
$apiProj = Join-Path $repo "src\Jarvis.Api\Jarvis.Api.csproj"
$summary = Join-Path $logs "milestone1-summary.txt"
$port    = 5185

New-Item -ItemType Directory -Force -Path $logs | Out-Null
"JARVIS Milestone 1 run - $(Get-Date -Format s)" | Set-Content $summary
function Log($msg) { Write-Host $msg; Add-Content $summary $msg }

Set-Location $repo
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

# 0. Stop a previous JARVIS instance so files are not locked
Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" |
    Where-Object { $_.CommandLine -like "*Jarvis.Api.dll*" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

Log "dotnet SDK: $(dotnet --version)"

# 1. Build
dotnet build "$repo\Jarvis.sln" -c Release *> "$logs\1-build.log"
if ($LASTEXITCODE -ne 0) { Log "BUILD: FAILED (see logs\1-build.log)"; exit 1 }
Log "BUILD: OK"

# 2. Automated tests
dotnet test "$repo\Jarvis.sln" -c Release --no-build --logger "console;verbosity=normal" *> "$logs\2-test.log"
$testLine = (Select-String -Path "$logs\2-test.log" -Pattern "(Passed!|Failed!)" | Select-Object -Last 1).Line
if ($LASTEXITCODE -ne 0) { Log "TESTS: FAILED - $testLine (see logs\2-test.log)" } else { Log "TESTS: OK - $testLine" }

# 3. API key in user-secrets (created once, never printed)
$secretsJson = dotnet user-secrets list --json --project $apiProj 2>$null | Out-String
$key = $null
try {
    $clean = ($secretsJson -replace '//BEGIN|//END', '').Trim()
    if ($clean) { $key = ($clean | ConvertFrom-Json).'Jarvis:ApiKey' }
} catch { }
if (-not $key) {
    $key = ([guid]::NewGuid().ToString("N")) + ([guid]::NewGuid().ToString("N"))
    dotnet user-secrets set "Jarvis:ApiKey" $key --project $apiProj | Out-Null
    Log "API KEY: created and stored in user-secrets"
} else {
    Log "API KEY: already set in user-secrets"
}

# 4. Publish a release build
dotnet publish $apiProj -c Release -o $publish *> "$logs\3-publish.log"
if ($LASTEXITCODE -ne 0) { Log "PUBLISH: FAILED (see logs\3-publish.log)"; exit 1 }
Log "PUBLISH: OK -> publish\"

# 5. Start JARVIS Core in the background (key passed via environment, not the command line)
$env:Jarvis__ApiKey = $key
$env:ASPNETCORE_URLS = "http://localhost:$port"
$env:ASPNETCORE_ENVIRONMENT = "Production"
# cmd /c gives a hidden, detached process whose output still goes to a log file.
$proc = Start-Process -FilePath "cmd.exe" `
    -ArgumentList "/c dotnet `"$publish\Jarvis.Api.dll`" > `"$logs\4-api.log`" 2>&1" `
    -WorkingDirectory $publish -WindowStyle Hidden -PassThru
Remove-Item Env:\Jarvis__ApiKey

$healthy = $false
for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Seconds 1
    try {
        $h = Invoke-RestMethod "http://localhost:$port/health" -TimeoutSec 2
        if ($h.status -eq "ok") { $healthy = $true; break }
    } catch { }
}
if (-not $healthy) { Log "DEPLOY: FAILED - API did not become healthy (see logs\4-api.log)"; exit 1 }
Log "DEPLOY: OK - JARVIS Core running at http://localhost:$port (PID $($proc.Id))"

# 6. Smoke test against the running API
& "$repo\scripts\smoke-test.ps1" -Key $key -BaseUrl "http://localhost:$port" *> "$logs\5-smoke.log"
$smokeLine = (Get-Content "$logs\5-smoke.log" | Select-String "passed," | Select-Object -Last 1).Line
Log "SMOKE: $smokeLine"
Log "DONE"
