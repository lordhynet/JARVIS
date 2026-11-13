# Client protocol

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
