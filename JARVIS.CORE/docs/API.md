# Client protocol

## Durable practice-plan workflows

These routes use the existing shared-key authentication and single-user server scope.
They are a foundation API, not the proposed OIDC/user-scoped mobile `/api/v1` contract.

| Method | Route | Behaviour |
|---|---|---|
| POST | `/api/workflows/practice-plans` | `{requestId,notBefore?}`; 202 with Location and canonical run |
| GET | `/api/workflows/paid-ai` | Paid-provider readiness and committed usage, never secrets |
| POST | `/api/workflows/interview-briefs` | `{requestId,allowCloudProcessing:true}`; gated paid draft, 202 |
| GET | `/api/workflows` | Latest 25 runs, newest first |
| GET | `/api/workflows/{id}` | Status, revision, attempts, deadline, safe error code and result |
| POST | `/api/workflows/{id}/cancel` | Revoke active claim; terminal runs remain unchanged |
| DELETE | `/api/workflows/{id}` | 204; clear content, retain a deduplication tombstone |
| GET | `/api/workflows/{id}/events?afterRevision=0` | Next 100 durable events in revision order |

`requestId` is a nonempty UUID and also the run ID. Retry the same request to retrieve
the same run; changing its requested due time returns 409. `notBefore` is optional UTC/
offset timestamp, up to seven days ahead. An initial request may be at most one minute
in the past. Replays remain valid after that window. A run's deadline is five minutes
after its requested due time. Missing runs return 404; invalid arguments return 400.

States: `Queued`, `Running`, `RetryScheduled`, `Succeeded`, `Failed`, `Cancelled`, `Expired`.
Results contain up to two due learning references (excluding French), one story reference,
generic practice instructions and explicit gaps. Resolve the current resource before
displaying/practising it; deleted resources must not be reconstructed from old results.
No model scoring, factual inference, email/calendar mutation or paid API call occurs.

The **practice-plan** workflow is unpaid. The separate `interview-brief` workflow calls
Claude only after the [paid AI configuration and consent gates](PAID_AI.md) pass. Its public
run has a nullable `brief` with summary, three drills, source IDs, model and generation time.
Deleted run IDs cannot be replayed to create another paid call. Briefs whose sources are
missing are not returned. Minimum deduplication/spend metadata survives result deletion.

Events contain event ID, run ID, revision, status and time, and commit atomically with
the run transition. Save the last received revision for polling/reconnect. No notifications
are pushed yet. Worker tokens, worker IDs and fingerprints are excluded from HTTP output.
Set `Jarvis:WorkflowsEnabled=false` to stop dispatch on a host; it does not revoke already
running claims. Use per-run cancellation to revoke those claims.

## Existing companion protocol

Send `X-Jarvis-Key: <key>` on every `/api` request. Bearer authentication is also
accepted. Query-string tokens are restricted to `/hubs` for SignalR transports.
JSON uses camelCase properties and string enum values. GUID request IDs provide
retry protection for reviews, messages and tool actions; conflicting reuse returns 409.

| Method | Route | Body or purpose |
|---|---|---|
| GET | `/health` | Public liveness |
| GET | `/health/ready` | Public database readiness |
| POST | `/api/sessions` | `activity`, optional `title`, optional `deviceId` |
| GET | `/api/sessions` | Sessions ordered by recent update |
| GET | `/api/sessions/{id}` | Session, checkpoints, transcript and handoff |
| POST | `/api/sessions/{id}/checkpoints` | `summary`, optional `currentTask` |
| POST | `/api/sessions/{id}/status` | `status`: Active, Paused or Ended |
| POST | `/api/sessions/{id}/claim` | `deviceId`; requires online verified device |
| POST | `/api/sessions/{id}/messages` | `requestId`, `text` |
| POST | `/api/sessions/{id}/handoff` | `fromDeviceId`, `toDeviceId`, `summary` |
| POST | `/api/sessions/{id}/handoff/{action}` | `handoffId`, `deviceId`; action ready/release/complete/cancel |
| GET | `/api/devices` | Devices, verification and online status |
| POST | `/api/devices` | `deviceId`, `name`, `kind`, `capabilities` |
| POST | `/api/devices/{id}/heartbeat` | Empty object; renew presence |
| POST | `/api/devices/{id}/verify` | Empty object; operator confirms hardware test |
| GET | `/api/learning?due=true` | Due queue; omit due to list all |
| POST | `/api/learning` | `domain`, `prompt`, `answer` |
| POST | `/api/learning/{id}/reviews` | `requestId`, `rating`, `responseSeconds` |
| GET | `/api/learning/{id}/reviews` | Review history |
| GET | `/api/tools` | Latest 100 audited actions |
| POST | `/api/tools/home` | `requestId`, `sessionId`, `entityId`, `action`, optional `triggeredByExternalContent` |
| POST | `/api/tools/{id}/confirm` | `approve`: true or false |
| GET | `/api/integrations` | Configuration status; never returns secrets |
| GET | `/api/transit?lines=jubilee,northern` | Live TfL line status |
| GET | `/api/calendar/{provider}?start=...&end=...` | google/microsoft, ISO timestamps with offset, maximum 31 days |
| GET | `/api/briefing` | Next 24 hours, transport, learning and per-source errors |
| POST | `/api/voice/transcribe` | Multipart `audio` file, maximum 10 MB |

Capabilities are `canListen`, `canSpeak`, `hasDisplay`, `canControlHome`, and an `extra`
string array. Registration resets verification. Native clients must stop microphone
and playback before sending `release`, and must not capture audio until they own the
session. Implement heartbeats and stop on connection loss rather than trusting stale state.

SignalR `/hubs/status` supports `Ping` and publishes `sessionEvent` with `type`,
`sessionId`, `status`, `activeDeviceId`, `at`. Re-fetch after events and reconnects.
Events are live hints, not a durable queue.

Errors: 400 validation, 401 authentication, 403 prohibited action, 404 missing record,
409 state/idempotency conflict, 429 cloud rate limit, 502 provider failure, 503 missing
configuration/readiness. Tool execution may return an audited Failed or Uncertain
status inside HTTP 200; inspect its status.

The shared-key API trusts all authorized clients. Device IDs are not credentials.
There are no per-user roles, per-device credentials or cross-user isolation.
# Story Bank

All `/api/stories` routes require the existing `X-Jarvis-Key` header.

| Method | Route | Result |
|---|---|---|
| GET | `/api/stories` | Stories ordered by last update, newest first |
| GET | `/api/stories/{id}` | Story, or 404 |
| POST | `/api/stories` | Create from `{ requestId, content }` |
| PUT | `/api/stories/{id}` | Replace content using `{ expectedVersion, content }` |
| DELETE | `/api/stories/{id}?expectedVersion=2` | Delete current version; 204 |

`content` contains `title`, `companyProject`, `serviceFeature`, `context`, `problem`,
`constraints`, `personalOwnership`, `technicalDecisions`, `tradeOffs`, `implementation`,
`collaboration`, `failureModes`, `result`, `metrics`, `changeToday`, `followUpQuestions`
and `cvClaims`. The last two are string arrays. Only title (200 characters), company/project
and service/feature (300 each) are required. Remaining prose fields accept 8,000 characters
each; each array accepts at most 30 nonblank entries of at most 2,000 characters.
Optional prose defaults to empty strings and arrays to empty arrays. Fields are trimmed.

Example create body:

```json
{
  "requestId": "3d57b7a6-cbf4-43a0-aafe-b992a7e10872",
  "content": {
    "title": "FX transaction module",
    "companyProject": "Alternative Bank",
    "serviceFeature": "FX transaction service"
  }
}
```

Use a new client-generated UUID for each story; it becomes the story ID. An identical
create retry returns the saved version-1 story; different content or an already edited
story returns 409. After deletion, creating that ID again creates a new story.
Updates increment `version` and preserve `createdAt`. Stale update/delete versions return
409 without changing state; reload and reconcile before retrying. Missing stories return
404 and invalid fields or nonpositive versions return 400. This backend does not infer
claims, generate performance metrics or automatically expose stories to cloud providers.

## Technical practice

The authenticated `/api/training` routes add a versioned question catalog, saved attempts,
self assessments, corrections, deletion and a dashboard. See [training API and semantics](TRAINING.md).

## Coaching, programme and session reviews

The authenticated coaching/programme routes add source-backed exercise prompts, saved
answers, corrections, explicit text observations, weekly self check-ins and derived session
reviews. See [coaching routes and semantics](COACHING.md). `/api/modules` exposes current
capabilities and remaining work, without presenting planned features as finished.

## Privacy and retention

Authenticated API responses include `X-Jarvis-Data-Epoch`. Clients with cached private data
can send their verified value in the same request header. A malformed or mismatched value
returns 409 with `coreEpochChanged: true` before normal endpoint dispatch. Privacy-control
routes remain accessible for status/recovery. The header is optional for existing clients;
Android 0.7.0 requires it on successful responses and clears its local data when its verified
generation changes, Core reports erasure or authentication is rejected. Store-level epoch
fences still reject work crossing an erasure boundary during execution.

Authenticated Core endpoints:

- `GET /api/privacy/status`: data epoch, revision and erased state.
- `GET /api/privacy/export`: consistent owner-data JSON download; excludes backend secrets and internal claims.
- `GET /api/privacy/retention`: configured retention policy.
- `GET /api/privacy/retention/preview`: cutoff, policy fingerprint and eligible counts.
- `GET /api/privacy/jobs`: latest content-free job records.
- `POST /api/privacy/retention/apply`: `requestId`, preview `asOf`, `policyFingerprint`, `confirmation: "APPLY RETENTION"`.
- `POST /api/privacy/erase`: `requestId`, `expectedRevision`, `confirmation: "ERASE MY CORE DATA"`.
- `POST /api/privacy/reopen`: `expectedRevision`, `confirmation: "START A NEW CORE"`; requires a Core API key different from the erasing key.

Normal Core operations return 503 while erased; work from an earlier epoch cannot publish after reopen. Erasure retains minimal spending/replay metadata and pauses paid work. See [privacy scope](PRIVACY.md) for data-copy limitations.

### Audio companion preparation

`POST /api/voice/prepare` accepts `CompanionVoiceRequest { requestId, deviceId, allowCloudProcessing }` and returns `{ sessionId }`. Requires authenticated, configured voice, explicit consent and an online verified audio device. Core selects/resumes/claims a canonical conversation; Android does not create or select sessions. A repeated request id resolves to the same prepared conversation and does not duplicate its checkpoint. An ended conversation or conflicting active audio lease returns conflict. Preparation itself does not dispatch a provider call. The subsequent ticket/WebSocket flow remains required.

`POST /api/voice/wake-candidates` accepts `{ requestId, deviceId, allowCloudProcessing: true }` after an opted-in Android local wake detector recognizes “Hey JARVIS”; it returns `{ sessionId, voice }` with the one-time voice ticket. It requires an authenticated, verified device, configured voice, prior live-voice cloud consent and the voice rate limit. Core prepares the canonical session and then uses the shared audio ownership lease as the cross-device arbitration fence; a competing active device receives conflict. Local detection itself uses Android's on-device speech service and has no cloud speech fallback. This route authorizes the subsequent live voice stream, which sends audio to the configured AI provider. Android 12 or newer and an available English on-device recognizer are required for local detection.

### Voice recall and review timing

The fixed realtime tool `core_recall` accepts `{ action, utterance_id, rating }`. Actions are `start/current/reveal/rate/stop`; use `None` for rating except when rating, and an empty utterance ID for current. Mutations require a captured user utterance from the current voice connection, with further phase/source checks on Core. No additional Android REST route is exposed.

`ReviewEvent` adds optional `timingSource`, defaulting to `user_reported` for existing reviews. Voice recall uses `not_measured`, with the legacy responseTime slot zero; consumers must not treat it as an actual zero-second performance measurement. Privacy export adds `voiceRecalls` phase/source-reference records. These are additive JSON/runtime fields and require no relational schema migration.

## GBP budget API (additive)

- `GET /api/budget/status`: Core-authenticated GBP readiness, scope/environment/month, ceiling, committed amount, unclassified legacy count, policy/conversion hashes, revision, approvals and public conversion basis. USD daily/month/lane checks remain additional restrictions. These are conservative normalized GBP amounts, not a claim of verified final card/invoice FX charges.
- The conversion basis includes the effective FX quote and automatic refresh status. `fxStatus` reports whether refresh is enabled, whether the automatic quote is in use, the last accepted automatic quote and the last attempt or error. ECB quotes include their publication date.
- `POST /api/budget/approvals`: owner-only `BudgetApprovalRequest { requestId, policyHash, month, ceilingGbp, expiresAt, expectedRevision, confirmation }`; exact confirmation `APPROVE MONTHLY AI CEILING`. Preview current status first. Approval is a total ceiling, not an increment; only current production month, >£100 and ≤£200.
- `POST /api/budget/approvals/{id}/revoke`: owner-only `{ requestId, expectedRevision, confirmation: "REVOKE AI CEILING APPROVAL" }`. Revocation blocks further authority but never refunds committed charges.
- `GET /api/budget/legacy/{id}/preview`: owner-only read of original USD amount, proposed frozen GBP commitment and conversion hash; does not assign or forgive any charge.
- `POST /api/budget/legacy/{id}/classify`: owner-only `{ requestId, policyHash, conversionHash, expectedUsd, evidence, confirmation: "ASSIGN LEGACY AI CHARGE" }`. Assigns reviewed old usage to the configured stable scope/environment at the previewed conservative conversion basis. Original reservation IDs/USD amounts/month stay intact; uncertain charges remain reserved. Current-month unclassified charges block new paid dispatch.

All management routes require `X-Jarvis-Owner-Key` in addition to normal Core authentication. Query credentials, a claimed actor/device ID or the ordinary Core key do not grant owner management. Missing setup returns 503; unauthorized management returns 403; stale preview/operation conflict returns 409; GBP exhaustion returns 429. Idempotent requests must retain their original request ID and payload.

The existing `/api/usage/charges/{id}/reconcile` route and body remain, but financial corrections now require the separate owner credential. It settles original USD and frozen-basis GBP accounting atomically; it cannot create a spending approval. Existing USD JSON fields are not renamed or reinterpreted.

Verified GBP billing correction is additive on the existing reconciliation request: optional `actualGbp` accepts a nonnegative whole-pence verified charge (up to the supported £10,000 accounting bound, not a spending authorization). Existing USD-only requests remain compatible. An already clean USD settlement must retain its exact USD amount when correcting GBP. The separate owner credential, billing evidence, ten-minute in-flight wait and one-reconciliation-per-charge protections apply. A legacy charge must be classified first. The frozen conversion basis remains in history; `GbpVerified: true` distinguishes an owner-verified GBP amount from conservative normalized accounting. A late duplicate USD settlement cannot overwrite verified GBP. Historical charges above an authorization ceiling are recorded truthfully and block new dispatch rather than being silently capped or erased.

### Core voice coaching tools

The existing realtime voice channel advertises `core_coaching_catalog` (no arguments) and `core_coaching_prompt` (`exercise_id`, nullable `story_id`, nullable `story_version`, nullable `claim_index`; all fields required). Both are read-only preparation tools. Story and CV exercises reuse Core's saved-story existence, version and claim-index checks. Saved story context supplies bounded claim previews and the current version; these are reference data, not verified evidence or instructions. Prompt text is bounded with an explicit truncation marker. These tools do not save coaching answers, start paid assessment or measure speaking pace.

### Voice coaching lifecycle

`core_coaching` now provides `start`, `current`, `answer` and `stop` within an active canonical session. Supply `action`, `utterance_id`, `exercise_id`, `story_id`, `story_version` and `claim_index`; all fields are required. Selection fields must be null except on `start`. `current` accepts an empty utterance ID; mutations require a captured user utterance in the current realtime window. The service checks that it is the session's latest user message. Answer text comes from Core's transcript, never from provider tool arguments.

Start persists the selected exercise and saved-story version. Current resumes the pending authored question after reconnect/restart. Answer atomically saves one canonical coaching attempt and marks the selection complete, with stable operation identity for retries. The start utterance cannot be used as an answer. Stop cancels the pending selection without deleting saved attempts; changing exercises requires stopping the current one. Realtime instructions require waiting for a finished user answer and prohibit saving questions or commands as answers. This classification still depends on the conversation model; no semantic answer classifier is claimed.

The response contains bounded prompt/answer text, word count, up to two text observations and suggested drills. No paid assessment, acoustic score or measured speech duration is generated. Existing text correction/deletion endpoints remain available. Full voice correction and automatic multi-exercise programme sequencing are future work. Saved source changes and deleted answers block replay. Privacy export includes `voiceCoachings`; erasure removes it, and retention removes aged/source-linked lifecycle metadata.

### Spoken coaching correction and feedback resume

`core_coaching current` now returns the pending question/correction, or the most recently updated completed voice exercise when none is pending. Its result includes the canonical answer `revision` and `attemptId`. Removed answers and changed saved sources remain blocked; Core does not silently substitute older feedback.

`core_coaching_correction` requires `action` (`prepare` or `answer`), `utterance_id`, `attempt_id` and `expected_revision`. Prepare must follow an explicit correction request. Core records a pending correction; the realtime assistant asks for a separate full replacement answer. Answer uses only the latest canonical user transcript and rejects the prepare utterance itself. Canonical correction and lifecycle completion are atomic, retain the original answer versions, clear obsolete AI assessment links and obey the existing five-version limit. A newer text/voice revision causes a conflict requiring refreshed feedback and a new correction request. Retries reuse the same correction operation identity.

Use `core_coaching stop` to cancel a pending correction while preserving the saved answer; cancellation returns no answer content, allowing it even after source deletion. Pending correction metadata survives a Core restart and is covered by privacy export, erasure and source-linked retention. These controls add no Android business logic, provider calls or acoustic measurements. Semantic intent classification still depends on realtime instructions.

## Owner and companion authorization update

Sensitive routes now additionally require `X-Jarvis-Owner-Key`: privacy export/erase/reopen/retention-apply; device verify and credential management; home-action confirm (including reject). Owner credentials are never accepted from query/body. Missing owner setup returns 503; invalid/missing owner headers return 403.

New owner routes: `POST /api/devices/{id}/credential` issues/rotates a companion secret for an existing registration; `DELETE` revokes it. The POST returns `{ deviceId, secret }` once per issuance. Rotation/revocation invalidate the device's active lease and pending tickets. Enrolled active registrations cannot be overwritten; revoke before changing their capabilities.

A companion sends its secret using the existing `X-Jarvis-Key` header. It can only read `/api/privacy/status`, `/api/voice/status`, and its filtered `/api/devices`; send its own heartbeat; prepare its own voice conversation and obtain its own voice tickets. Every other API/hub route is denied, even when a companion supplies an owner header. Request-body device identity must match the authenticated device. No arbitrary grants or roles can be supplied by a client. Existing voice WebSocket tickets remain short-lived and single-use.

## Windows locked voice boundary (Item 15a)

Voice ticket and wake-candidate admission accept optional `locked: true|false`. Native Windows registrations (`WindowsAgent`) without a lock report are restricted. Historical desktop browsers also use that kind; their existing Core registration must have both `hasDisplay: true` and the `browser-companion` tag to retain the browser default. This exemption cannot be asserted in a heartbeat. Existing Android clients retain their supported default. The request's device ID must still match the authenticated companion and the existing audio claim.

WebSocket heartbeat example: `{ "type": "heartbeat", "sequence": 1, "locked": true }`. Windows clients must report lock state on each heartbeat. Missing state is restricted; non-boolean values are refused. A lease now also returns optional-compatible `locked` and nullable `reportedLocked` metadata. These fields are server-owned. Echoing a different value in an old lease cannot relax policy or bypass its session/device/fence checks.

An unlock report is applied only when Core activates the next window. A newly reported lock during a window that already received personal context ends the connection with code/reason `device_locked`; the client stops audio and returns to local listening. A later consented claim starts a restricted stream. Core does not replay the previous paid request.

Locked windows receive no saved conversation/task/practice context or private persona. Personal tools are neither advertised nor executable. The installed home tool uses only effective grants explicitly carrying `allowWhenLocked: true`. This flag is optional/default false on grant requests and views, is audited with the existing grant, and cannot be changed by reusing a grant request ID. It does not replace pre-approval or sensitive-action owner confirmation. Existing grants remain false after migration.

Terminal budget failures retain their error detail and emit a specific ended reason: `serious_daily_cap` or `gbp_budget_ceiling`. A successful retry after the previous window settles remains permitted by the existing accounting rules.

See [the locked voice ADR](adr/0004-locked-voice-boundary.md). The Windows tray app and travel tool are subsequent implementations.
