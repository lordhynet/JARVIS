$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$target = Join-Path $root 'appsettings.json'
if (Test-Path -LiteralPath $target) { Write-Host 'Canonical appsettings.json already exists; no credentials changed.'; return }
$defaultsPath = Join-Path $root 'src/Jarvis.Api/appsettings.json'
if (!(Test-Path -LiteralPath $defaultsPath)) { $defaultsPath = Join-Path $root 'appsettings.example.json' }
$config = Get-Content -LiteralPath $defaultsPath -Raw | ConvertFrom-Json -AsHashtable
$legacy = @{}
$legacyPath = Join-Path $root '.env'
if (Test-Path -LiteralPath $legacyPath) {
    foreach ($line in [IO.File]::ReadAllLines($legacyPath)) {
        if ($line -match '^([A-Z][A-Z0-9_]*)=(.*)$') { $legacy[$Matches[1]] = $Matches[2].Trim().Trim([char[]]@('"', "'")) }
    }
}
function Existing([string]$name, [string]$fallback) { if ($legacy.ContainsKey($name) -and $legacy[$name].Length -gt 0) { return $legacy[$name] }; return $fallback }
$config['Jarvis'] = @{ ApiKey=(Existing 'JARVIS_API_KEY' ([Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)))); StorageProvider='Postgres'; WorkflowsEnabled=$true }
$config['Database'] = @{ Host='postgres'; Port=5432; ExposedPort=15432; Name='jarvis'; Username='jarvis'; Password=(Existing 'JARVIS_POSTGRES_PASSWORD' ([Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)))) }
$config['Server'] = @{ BindAddress='127.0.0.1'; HttpPort=5185; HttpsPort=5186; AndroidBridgeUrl='https://127.0.0.1:5186';
    MaxRequestBodyBytes=11000000;
    Container=@{ BindAddress='0.0.0.0'; HttpPort=8080; HttpsPort=8443; CertificatePath='/run/https/server.pfx' };
    Windows=@{ BindAddress='127.0.0.1'; HttpPort=5185; HttpsPort=5186; CertificatePath=(Join-Path $root '.https/server.pfx') };
    Tls=@{ Password=(Existing 'JARVIS_TLS_PASSWORD' ([Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)))); Enabled=$true } }
$config['OpenAI']['ApiKey'] = Existing 'OPENAI_API_KEY' ''
$config['OpenAI']['Model'] = Existing 'OPENAI_MODEL' 'gpt-4.1-mini'
$config['OpenAI']['TranscriptionModel'] = Existing 'OPENAI_TRANSCRIPTION_MODEL' 'gpt-transcribe'
$config['AI']['Chat']['Model'] = $config['OpenAI']['Model']
$config['AI']['Transcription']['Model'] = $config['OpenAI']['TranscriptionModel']
$config['Anthropic'] = @{ ApiKey=(Existing 'ANTHROPIC_API_KEY' ''); Model=(Existing 'ANTHROPIC_MODEL' 'claude-sonnet-4-6') }
$config['PaidAI']['Enabled'] = $true
foreach ($item in @{DailyLimitUsd='PAID_AI_DAILY_LIMIT_USD';MonthlyLimitUsd='PAID_AI_MONTHLY_LIMIT_USD';BackgroundLimitUsd='PAID_AI_BACKGROUND_LIMIT_USD'}.GetEnumerator()) {
    $value = [decimal]0
    if ($legacy.ContainsKey($item.Value) -and [decimal]::TryParse($legacy[$item.Value], [ref]$value) -and $value -gt 0) { $config['PaidAI'][$item.Key] = $value }
}
$config['PaidAI']['InputUsdPerMillion'] = [decimal](Existing 'PAID_AI_INPUT_USD_PER_MILLION' '3')
$config['PaidAI']['OutputUsdPerMillion'] = [decimal](Existing 'PAID_AI_OUTPUT_USD_PER_MILLION' '15')
$config['PaidAI']['RateCardDate'] = Existing 'PAID_AI_RATE_CARD_DATE' '2026-11-13'
$config['PaidAI']['RateCardModel'] = Existing 'PAID_AI_RATE_CARD_MODEL' $config['Anthropic']['Model']
$config['HomeAssistant'] = @{ BaseUrl=(Existing 'HOME_ASSISTANT_URL' ''); Token=(Existing 'HOME_ASSISTANT_TOKEN' ''); AllowedEntities=@((Existing 'HOME_ASSISTANT_ENTITY' '')) }
$config['Calendar'] = @{ Google=@{ AccessToken=(Existing 'GOOGLE_CALENDAR_TOKEN' ''); CalendarId='primary' }; Microsoft=@{ AccessToken=(Existing 'MICROSOFT_CALENDAR_TOKEN' '') } }
$config['TfL'] = @{ AppKey=(Existing 'TFL_APP_KEY' '') }
[IO.File]::WriteAllText($target, ($config | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
# The example is documentation/build-test defaults only; the running backend reads appsettings.json.
$example = $config | ConvertTo-Json -Depth 20 | ConvertFrom-Json -AsHashtable
$example.Jarvis.ApiKey=''; $example.Database.Password=''; $example.Server.Tls.Password=''; $example.OpenAI.ApiKey=''; $example.Anthropic.ApiKey=''
$example.HomeAssistant.Token=''; $example.Calendar.Google.AccessToken=''; $example.Calendar.Microsoft.AccessToken=''; $example.TfL.AppKey=''
$example.Server.Windows.CertificatePath='.https/server.pfx'
[IO.File]::WriteAllText((Join-Path $root 'appsettings.example.json'), ($example | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
Write-Host 'Migrated backend settings into the private root appsettings.json. Credentials were preserved and not printed.'
