# JARVIS — Milestone 1: session core

The first runnable piece of JARVIS Core: an ASP.NET Core (.NET 10) service that creates,
reads and checkpoints conversation sessions, pushes live events to devices over SignalR,
and is protected by an API key.

## What is built (and what is not)

| Built and tested | Not yet (next milestones) |
|---|---|
| Shared contracts: devices, sessions, checkpoints, handoff, tools, learning items | PostgreSQL persistence (sessions are in memory and reset on restart) |
| `POST/GET /api/sessions`, `GET /api/sessions/{id}`, `POST /api/sessions/{id}/checkpoints` | Cloud voice, wake word, microphone |
| Live status hub at `/hubs/status` (SignalR) | Home Assistant, calendar, TfL |
| Public `/health` endpoint for the watchdog | Learning engine logic (contracts only) |
| API-key protection on `/api` and `/hubs` (fails closed) | Device registration endpoints |

## Layout

```
JARVIS/
├─ Jarvis.sln
├─ global.json               .NET 10 SDK
├─ Directory.Build.props     shared settings (nullable, warnings as errors)
├─ src/
│  ├─ Jarvis.Contracts/      records and enums shared by every client
│  ├─ Jarvis.Orchestrator/   SessionService + ISessionStore (in-memory for now)
│  └─ Jarvis.Api/            minimal API, SignalR hub, API-key middleware
├─ tests/
│  └─ Jarvis.Tests/          xUnit integration tests (REST + SignalR)
└─ scripts/
   └─ smoke-test.ps1         end-to-end check against the running API
```

## Run it on the Dell

Open **PowerShell** in this folder (in File Explorer, click the address bar, type `powershell`, press Enter).

**1. Check the SDK**
```powershell
dotnet --version        # expect 10.0.x
```

**2. Restore and build**
```powershell
dotnet build
```
Expect: `Build succeeded. 0 Warning(s) 0 Error(s)`.

**3. Set your API key (once).** Pick any long random string. It is stored in your Windows
user profile, not in this folder, so it never ends up in Git or OneDrive.
```powershell
cd src\Jarvis.Api
dotnet user-secrets set "Jarvis:ApiKey" "$(New-Guid)$(New-Guid)"
dotnet user-secrets list     # shows the key so you can copy it
cd ..\..
```

**4. Start JARVIS Core**
```powershell
dotnet run --project src\Jarvis.Api
```
Expect: `Now listening on: http://localhost:5185`. Leave this window open.

**5. Health check** — open http://localhost:5185/health in a browser.
Expect: `{"status":"ok","service":"jarvis-core",...}`.

**6. Smoke test** — in a second PowerShell window in this folder:
```powershell
.\scripts\smoke-test.ps1 -Key "<paste your key>"
```
Expect: `7 passed, 0 failed`. If PowerShell blocks the script, run
`Set-ExecutionPolicy -Scope CurrentUser RemoteSigned` once.

**7. Automated tests** (stop the API first with Ctrl+C, or leave it — tests use their own in-memory server)
```powershell
dotnet test
```
Expect: `Passed! - Failed: 0, Passed: 11`.

## Pass criteria for Milestone 1

- [ ] `dotnet build` — 0 warnings, 0 errors
- [ ] `/health` returns ok without a key
- [ ] `/api/sessions` returns 401 without a key and 503 if no key is configured
- [ ] Smoke test: 7 passed
- [ ] `dotnet test`: 11 passed, including a device receiving a live `session.created` event

## Notes

- **OneDrive:** this folder is inside OneDrive, so `bin/` and `obj/` will sync on every
  build. That is harmless but noisy. When convenient, move the folder to
  `C:\Users\surak\source\JARVIS` or exclude it from OneDrive sync. Database data
  (Milestone 2) will live in a Docker volume, not in this folder.
- **Never** put the API key in `appsettings.json` or commit it.

## Next: Milestone 2

PostgreSQL in Docker (`docker compose up -d`), an EF Core `PostgresSessionStore` behind
`ISessionStore`, and a test that proves a session survives an API restart.
