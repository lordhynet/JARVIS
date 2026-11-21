Current Android direction: audio-only earbuds companion; all features remain on Core. See [audio-only architecture](docs/adr/0002-audio-only-android-core-owned-product.md), [Core voice tools](docs/LIVE_VOICE.md) and [remaining Core work](docs/CORE_BACKLOG.md). Older release evidence below is historical.

# JARVIS — personal companion

JARVIS now includes the seven implementation areas in [the milestone scope](docs/MILESTONES.md): sessions, PostgreSQL persistence, devices and handoff, learning, integrations and tool auditing, conversation and voice, and a browser companion with deployment tooling.

This is a single-user application with a shared API key. The browser runs on desktop and mobile.
An installable Kotlin/Compose Android personal test build is in the sibling `JARVIS.MOBILE`
folder; [Android setup](../JARVIS.MOBILE/README.md) describes its scope and release gaps.
Quest apps and satellite firmware are not included. Cloud accounts and voice hardware still need verification.

## Run

For this machine's persistent Docker deployment, see [server operation](docs/SERVER.md).

Requires .NET 11 SDK (11.0.100 or newer within .NET 11) and Docker Desktop for PostgreSQL. The workspace-local SDK is available at `../.tools/dotnet/dotnet.exe`; `scripts/test-backend.ps1` selects it automatically. Start Docker Desktop, then:

```powershell
# New installations only: copy the template, then fill in private values.
# Do not overwrite an existing appsettings.json.
Copy-Item appsettings.example.json appsettings.json
# Set Jarvis.ApiKey and Database.Password in appsettings.json before starting.
.\scripts\migrate-schema.ps1
.\scripts\start-server.ps1
```

Open [the companion](http://localhost:5185), enter `Jarvis.ApiKey` from the private, Git-ignored root `appsettings.json`, and create a conversation. Preserve `Database.Password` after initializing the database volume. The server reads this JSON file as its sole deployment configuration; `.env`, user-secrets, environment-specific JSON and environment overrides are not runtime configuration sources.

For local Windows execution, use the .NET 11 SDK. With PostgreSQL ready, writers stopped and the backup verified, run `dotnet run --no-launch-profile --project src/Jarvis.Api -- --migrate-only`, then `dotnet run --no-launch-profile --project src/Jarvis.Api`. Both use the same root settings, including `Server.Windows`, and the configured exposed PostgreSQL port. See [schema compatibility](docs/SCHEMA_COMPATIBILITY.md). Cloud integrations remain unavailable until their keys are configured.

Open the HTTP address above, not `index.html` directly from File Explorer: the stylesheet,
JavaScript and API are served together. PostgreSQL uses local port 15432 to avoid other
projects on port 5432; change `Database.ExposedPort` in `appsettings.json` if needed.

## What works

| Area | Implementation |
|---|---|
| Sessions | Create/list/read, checkpoints, pause/resume/end, SignalR status events |
| Persistence | EF Core migrations, PostgreSQL stores, atomic updates and upgrade support |
| Devices | Registration, heartbeat, capability verification, online status |
| Handoff | Checkpoint → target ready → source audio release → target ownership; timeout recovery |
| Learning | Shared due queue, four manual review ratings, persistent history and retry-safe reviews |
| Technical practice | 16 questions across eight domains, Core-owned authored drills with browser and voice interfaces, saved answers, explicit self assessments, corrections and 1/3/7/14-day review scheduling; [scope](docs/TRAINING.md) |
| Communication/interview coaching | 24 structured exercises, real-story/CV-claim prompts, versioned answers and explicit text observations; [scope](docs/COACHING.md) |
| Session reviews & programme | Current cross-module reviews, local browser export, twelve-week focus plans and weekly self check-ins |
| Browser modules | Story editor, technical practice, coaching, programme, workflow controls and module status; [coverage](docs/IMPLEMENTATION_STATUS.md) |
| Practice workflows | Durable deterministic plans from due cards/stories, status events, cancellation, leased workers and bounded retries |
| Paid interview drafts | Claude adapter, explicit cloud consent, budget reservations, validated source references and uncertain-charge protection; disabled until configured |
| Conversation | OpenAI Responses API, activity coaching, saved transcript and duplicate-message protection |
| Voice | Browser microphone recording → cloud transcription → reviewed message; browser speech output; opt-in foreground wake word |
| Home | Allowlisted Home Assistant lights/switches, explicit confirmations, persistent audit and uncertain-outcome recovery |
| Briefing | Google/Microsoft calendar reads, live TfL status and due learning; source-specific outage reporting |
| Operations | Readiness endpoint, non-root Docker image, persistent Compose stack, backup and non-overwriting restore |

Follow [configuration and device setup](docs/CONFIGURATION.md) to connect the external services. Calendar adapters currently accept current OAuth access tokens; automatic account linking and token refresh are not implemented. Wake-word support depends on the browser and is not an always-on background service. Speech output uses the browser's voice rather than a cloud TTS model.

Development now follows the [master requirements](docs/JARVIS_MASTER_REQUIREMENTS_SINGLE_PRODUCT_2026-11-13.md).
Android uses Kotlin/Jetpack Compose; see the [integration ADR](docs/adr/0001-unified-product-and-workflow-foundation.md)
for the repository map, requirements precedence and pending mobile/agent gates.
The Story Bank backend adds persistent draft stories with version-checked editing and deletion;
see the [API reference](docs/API.md). Story editing in the browser and interview-coach integration
are still pending. The [delivery scope](docs/MILESTONES.md) tracks remaining P0 areas.

Practice workflows execute locally without model calls: Core selects due learning references
and a saved story, persists the plan and exposes status/events through `/api/workflows`.
This is an agent-runtime foundation slice, not the full proactive AI interview-brief workflow.

The separate [paid AI workflow](docs/PAID_AI.md) generates a preparation draft and three drills
when enabled with an API key and owner budgets. Calendar triggers and independent specialist
coordination remain pending. Android can be paired over a private certificate-pinned debugging
bridge without typing a link or Core key.

## Tests

Latest [verification results](docs/VERIFICATION.md): 82 backend tests passed with real PostgreSQL,
14 browser tests passed, and the signed native APK passed four unit tests and lint checks.
It was installed and connected on the user's Samsung phone. The Production server was upgraded
after backup; paid calls remain disabled until configured. Scripts use PowerShell 7 (`pwsh`).

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

Keep the existing private `appsettings.json`, including its API key and database password, then:

```powershell
.\scripts\start-server.ps1
.\scripts\backup-database.ps1
.\scripts\restore-database.ps1 -BackupPath '<backup.dump>' -NewDatabaseName jarvis_restore_check
```

Before a schema upgrade, stop all Core writers and verify the backup, then run `scripts/migrate-schema.ps1` with the intended build before starting Core. The script refuses a running API container in its Compose project; writers outside that project remain the operator's responsibility. Ordinary starts never migrate implicitly.

The API and database ports bind to loopback. For other devices, use a trusted HTTPS reverse proxy/VPN and keep the API key private. The restore script always creates a separate database and fails if that name exists. Inspect restored data before switching the connection string. `docker compose down` preserves data; `docker compose down -v` deletes the volume.

`/health` checks the process; `/health/ready` checks database connectivity. Startup refuses unknown or pending migrations; the designated migration-only command applies schema changes separately. Database commits precede SignalR notifications; there is no durable notification replay. The runtime document serializes updates and is suitable for a personal assistant, not a high-throughput multi-tenant service.

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
