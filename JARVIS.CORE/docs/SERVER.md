# Running this machine as the JARVIS server

JARVIS runs in Docker Compose with PostgreSQL on this Windows machine.
The companion is available at http://localhost:5185 while Docker Desktop is running.
Both containers restart automatically when Docker starts; Docker Desktop must be started
after a machine reboot. Database data lives in the `jarvis-postgres` Docker volume.

The private, Git-ignored `appsettings.json` file in the repository root contains `Jarvis.ApiKey`
and `Database.Password`, plus all server, TLS, provider, model, rate and integration settings. Copy `Jarvis.ApiKey` into the companion's Connect form.
Keep the existing database password when restarting or upgrading.

Run these commands from the repository root:

For first initialization or a schema upgrade, use [the designated migrator](SCHEMA_COMPATIBILITY.md): stop all writers, verify the backup, then run `scripts/migrate-schema.ps1` with the intended build. It checks for a running API container in its Compose project and does not start Core. Normal startup refuses pending or unknown migrations; `MigrateOnStartup=true` no longer bypasses this check. Do not run a live migration without the existing release/owner gates.

```powershell
# Start, or rebuild after changing application code
./scripts/start-server.ps1

# Status and logs
docker ps --filter label=com.docker.compose.project=jarvis
docker logs --tail 100 jarvis-api-1
Invoke-RestMethod http://localhost:5185/health/ready

# Stop the server; retain its database
docker stop jarvis-api-1 jarvis-postgres-1

# Back up the database
.\scripts\backup-database.ps1
```

Set `OpenAI.ApiKey`, `OpenAI.Model` and `OpenAI.TranscriptionModel` in `appsettings.json` to enable
AI replies and transcription, then run the start command again to recreate the API with
the updated configuration. External accounts are not configured automatically. See
[configuration](CONFIGURATION.md) for the other integrations.

The current deployment binds to loopback. Phone and other-device access requires a trusted
HTTPS reverse proxy or VPN as described in the configuration guide.

## Automatically pair the connected Android test app

The signed native APK can receive its Core address, connection key and exact TLS certificate
fingerprint through the already authenticated USB/wireless debugging connection. It saves
accepted credentials in Android Keystore-backed encrypted storage. No address/key typing,
system trust-store change, TLS bypass or public listener is required.

```powershell
./scripts/prepare-mobile-https.ps1
./scripts/start-server.ps1 -NoBuild
./scripts/connect-android.ps1 -Device '<adb device serial or IP:connection-port>'
```

Tap **Connect to my Core** on the phone. The optional Compose override binds HTTPS only to
`127.0.0.1:5186`; `adb reverse` carries that port to the phone, which authenticates the exact
provisioned certificate while retaining hostname/date validation. Private certificate and
password material remain in Git-ignored `.https` and `appsettings.json`. Rotate an expired certificate
and pair again; never work around a fingerprint mismatch by disabling verification.

This is a **development connection**, available while ADB remains connected. A phone/Wi-Fi
reset may change the debugging port; reconnect ADB and rerun the pairing script. Saved app
credentials survive normal app restarts. Away-from-home operation still needs a persistent
trusted HTTPS/VPN deployment. Keep the mobile override in future Compose start/update commands
when this bridge is in use. The original loopback HTTP/browser endpoint stays available.

For future manual connections to an existing public-trust HTTPS deployment, Android's
**Already have a server?** section remains available. Provider keys stay exclusively on Core;
see [paid AI setup](PAID_AI.md) before enabling Claude requests.

## Workflow worker

The same API container runs the practice-plan worker. Startup applies the additive
`WorkflowRuns` migration; back up before upgrades. Run status and durable events are
available through the authenticated [workflow API](API.md#durable-practice-plan-workflows).
No paid provider is called by this workflow. Existing conversation integrations still use
their configured providers independently.

`Jarvis:WorkflowsEnabled=false` in the API configuration disables dispatch on that host
after restart. Every participating host must have dispatch disabled to stop new claims
globally. Already running claims require per-run cancellation; a cancelled/expired token
cannot publish a result. Shutdown leaves unfinished claims recoverable after their
30-second lease expires. Temporary diagnostic run IDs and deployment evidence are in
Git-ignored `logs/workflow-server-verification.json`.
