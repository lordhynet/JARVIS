# ADR 0001: Unified product, native Android and durable workflow foundation

Date: 13 November 2026. Status: accepted for incremental implementation.

## Authority and repository map

The [master requirements](../JARVIS_MASTER_REQUIREMENTS_SINGLE_PRODUCT_2026-11-13.md),
especially sections 2.4, 19, 21 and 26, are the current authority.
The [Android v1.0 handoff](../JARVIS_ANDROID_COMPANION_REQUIREMENTS_2026-11-13.md)
is supplementary wherever compatible. Its React Native/Expo stack conflicts with the
master's explicit Kotlin/Jetpack Compose decision: use Kotlin/Compose. Its Expo-specific
tooling, JavaScript bridge and SQLite decisions do not govern the native implementation.
Record remaining differences before implementing the affected slice; do not silently
rewrite either source document or treat all Android v1.0 release boundaries as current.

| Location | Observed implementation | Direction |
|---|---|---|
| `JARVIS.CORE` | .NET 10 API, CQRS, PostgreSQL, browser companion | Shared orchestration, identity, canonical state and policies |
| `JARVIS.FRONTEND` | Empty at discovery | Future client of the same Core; current web assets remain in Core |
| `JARVIS.MOBILE` | Empty at discovery | Thin Kotlin/Compose client; no independent assistant or memory |
| `Jarvis.Api/Security` | Single shared key | Existing local-server boundary; OIDC/PKCE and user scoping remain required |
| `Jarvis.Orchestrator/Sessions` | Durable sessions and checkpoints | Reuse; global audio leases/fencing still required |
| `Runtime/RuntimeStore` | Devices, learning and Story Bank | Reuse canonical content; do not create mobile-owned copies |
| `Jarvis.Api/RuntimeWorker` | Handoff/tool recovery | Remains; workflow execution has its own hosted worker |
| `Jarvis.Persistence` | EF Core/Npgsql and migrations | Add dedicated orchestration tables in the existing database |

## First executable workflow slice

Implement version-1 `practice-plan`: select up to two due non-French learning items
and the least recently edited story, then return resource references and general practice
instructions. This is deterministic planning, not an AI-generated interview brief or a
claim of mastery. Missing source material produces explicit gaps. Source text and answers
are not copied into workflow results or event history. Clients resolve references through
the canonical stores and handle missing/deleted resources.

Persist runs separately from the shared runtime document. A client UUID identifies one
manual trigger; a request fingerprint detects conflicting reuse. Mutations append a
content-free, revision-ordered event in the same database transaction as the run update.
Clients poll status/events; this event log is not a notification outbox dispatcher.

Workers claim one due row with `FOR UPDATE SKIP LOCKED`, commit before processing,
and use a unique claim token with a 30-second lease. All completion, failure and heartbeat
writes validate that the token is still current and unexpired. Cancellation revokes the
token. Expired worker claims can be recovered by another host, with at most three attempts.
Transient retries use bounded exponential delay and jitter; runs expire five minutes
after their requested due time. Future manual scheduling is limited to seven days.

Keep the current shared-key `/api` surface compatible. Worker tokens and fingerprints are
never serialized in HTTP responses. These routes are not the proposed user-scoped
`/api/v1` mobile contract, and must not be presented as release-ready mobile authentication.

## Gates and remaining work

This establishes only part of SW-01, SW-02, SW-05, SW-06, SW-07, SW-09 and SW-10.
It does not complete the agent-runtime foundation or Android M0. Separate persisted task/
attempt records, notification outbox dispatch, calendar triggers, pause controls, budgets,
policy gateway, provider routing, multi-specialist synthesis, telemetry, retention/deletion
operations and OIDC/user isolation remain pending. No paid provider or external tool is
called by this workflow, and none may be added before its permission and budget gates.

Java 17 was found locally. No Android SDK or adb was found at discovery. A pinned native
toolchain and actual Samsung/headset tests remain necessary before claiming an APK,
screen-off voice, routing, interruption handling or audio feasibility. The server currently
binds to loopback; mobile use also needs authenticated, trusted HTTPS reachability.
