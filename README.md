# JARVIS — personal companion

JARVIS now includes the seven implementation areas in [the milestone scope](docs/MILESTONES.md): sessions, PostgreSQL persistence, devices and handoff, learning, integrations and tool auditing, conversation and voice, and a browser companion with deployment tooling.

This is a single-user application with a shared API key. The browser runs on desktop and mobile. Native Android/Quest apps and satellite firmware are not included. Real cloud accounts and physical hardware still need to be connected and verified.

## Run

Requires .NET 10 and Docker Desktop for PostgreSQL. Start Docker Desktop, then:

```powershell
dotnet build
# Only if you have not already configured a JARVIS API key:
dotnet user-secrets set --project src/Jarvis.Api "Jarvis:ApiKey" "<your long random key>"
.\scripts\run-milestone2.ps1
```

Open [the companion](http://localhost:5185), enter your JARVIS API key, and create a conversation. The script creates a database password in Git-ignored `.env`, starts PostgreSQL, and enables persistent storage. Keep `.env` private; don't change its password after initializing the volume.

For a quick in-memory session, use `dotnet run --project src/Jarvis.Api`. It warns that data will be lost on restart. Cloud integrations remain unavailable until configured; they never return mock successes in the application.

Open the HTTP address above, not `index.html` directly from File Explorer: the stylesheet,
JavaScript and API are served together. PostgreSQL uses local port 15432 to avoid other
projects on port 5432; override with `run-milestone2.ps1 -DatabasePort <port>` if needed.

## What works

| Area | Implementation |
|---|---|
| Sessions | Create/list/read, checkpoints, pause/resume/end, SignalR status events |
| Persistence | EF Core migrations, PostgreSQL stores, atomic updates and upgrade support |
| Devices | Registration, heartbeat, capability verification, online status |
| Handoff | Checkpoint → target ready → source audio release → target ownership; timeout recovery |
| Learning | Five domains, due queue, four review ratings, persistent history, retry-safe reviews |
| Conversation | OpenAI Responses API, activity coaching, saved transcript and duplicate-message protection |
| Voice | Browser microphone recording → cloud transcription → reviewed message; browser speech output; opt-in foreground wake word |
| Home | Allowlisted Home Assistant lights/switches, explicit confirmations, persistent audit and uncertain-outcome recovery |
| Briefing | Google/Microsoft calendar reads, live TfL status and due learning; source-specific outage reporting |
| Operations | Readiness endpoint, non-root Docker image, persistent Compose stack, backup and non-overwriting restore |

Follow [configuration and device setup](docs/CONFIGURATION.md) to connect the external services. Calendar adapters currently accept current OAuth access tokens; automatic account linking and token refresh are not implemented. Wake-word support depends on the browser and is not an always-on background service. Speech output uses the browser's voice rather than a cloud TTS model.

## Tests

Latest [verification results](docs/VERIFICATION.md): 53 backend tests and 14 browser tests passed after the CQRS refactor. Earlier deployment checks also verified real container restart, backup/restore and live TfL. The verification scripts use PowerShell 7 (`pwsh`).

```powershell
# Fast API, orchestration and simulated provider tests; database tests show as skipped:
dotnet test

# All backend tests using the Docker PostgreSQL instance:
.\scripts\run-milestone2.ps1 -Test

# Or use an isolated native PostgreSQL installation on Windows:
.\scripts\test-local.ps1 -PostgresBin 'C:/Program Files/PostgreSQL/18/bin'

# Real browser interactions against a running test API (desktop and mobile viewport):
npm ci
npx playwright install chromium
npm run test:browser

# Isolated full container deployment, restart, backup/restore and optional public live TfL:
.\scripts\test-deployment.ps1 -LiveTransit
```

[Verification and live scenarios](docs/TESTING.md) distinguish automated, simulated-provider, live-service and physical-device checks. Database tests create randomly named test databases and drop only those databases. The deployment script creates a random Compose project, then removes only its own containers and test volume. Native test clusters are stopped and their logs retained in the temporary directory.

## Deploy and back up

Add `JARVIS_API_KEY` to your private `.env` alongside `JARVIS_POSTGRES_PASSWORD`, then:

```powershell
docker compose -f compose.yaml -f compose.app.yaml up -d --build
.\scripts\backup-database.ps1
.\scripts\restore-database.ps1 -BackupPath '<backup.dump>' -NewDatabaseName jarvis_restore_check
```

The API and database ports bind to loopback. For other devices, use a trusted HTTPS reverse proxy/VPN and keep the API key private. The restore script always creates a separate database and fails if that name exists. Inspect restored data before switching the connection string. `docker compose down` preserves data; `docker compose down -v` deletes the volume.

`/health` checks the process; `/health/ready` checks database connectivity. Migrations run during startup. Database commits precede SignalR notifications; there is no durable notification replay. The runtime document serializes updates and is suitable for a personal assistant, not a high-throughput multi-tenant service.

Custom clients can use the [API protocol reference](docs/API.md).

## Structure

The application uses [CQRS with separate command/query handlers](docs/ARCHITECTURE.md),
read-only query ports and transactional write services. Both sides share the database.

- `src/Jarvis.Contracts`: shared API and state records.
- `src/Jarvis.Orchestrator`: sessions, devices, handoff, learning, chat and tool policy.
- `src/Jarvis.Persistence`: EF Core model, migrations and PostgreSQL stores.
- `src/Jarvis.Api`: HTTP/SignalR endpoints, provider adapters and the browser companion.
- `tests/Jarvis.Tests`: API, scenario, provider-contract and PostgreSQL tests.
- `tests/browser`: Playwright desktop/mobile workflows.
- `scripts`: launch, live checks, deployment verification and backup/restore.

Generate schema changes with `dotnet tool restore`, then `dotnet ef migrations add <Name> --project src/Jarvis.Persistence`. Check drift with `dotnet ef migrations has-pending-model-changes --project src/Jarvis.Persistence`.
