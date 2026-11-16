# Verification and real-life scenarios

Test evidence is generated locally: `.trx` results in `tests/Jarvis.Tests/TestResults`,
browser traces/screenshots in `test-results`, an HTML report in `playwright-report`, and
deployment backup/report artifacts under `TestResults/jarvis-verify-*`. These are ignored
by Git. Automated provider simulations are confined to the test project.

## Automated scenario coverage

| Scenario | What is exercised |
|---|---|
| Start morning practice | Create a session, save progress, read it back |
| Move laptop → phone | Verified device heartbeats, checkpoint, target ready, source release, target claim |
| Phone disconnects during transfer | Expiration, no premature target ownership, retained context and reclaim |
| Two devices claim at once | Exactly one owner and a conflict for the other |
| Pause or finish practice | Paused/ended sessions reject chat; ended sessions cannot resume or checkpoint |
| Network retry after a reply | Same request ID returns saved transcript without another model call |
| Learn a French phrase | Due queue, Again/Hard/Good/Easy intervals, duplicate review protection |
| Turn on the desk light | No side effect before confirmation; concurrent confirmations execute once |
| Malicious external text | Explicit external-origin home requests are rejected |
| Device response is lost | Audited uncertain outcome and no automatic repeat |
| API stops during a home action | Stale running action becomes uncertain without reexecution |
| Calendar unavailable | Briefing still returns available transport and learning sections |
| Recurring/all-day calendar results | Provider pagination, date-only preservation, cancelled entries excluded |
| Calendar pagination redirects token | Untrusted next-link host rejected |
| Recorded voice | Multipart upload shape, extension validation, reviewable transcript |
| Old installation upgrade | Initial milestone-2 schema upgraded with existing session data |
| API restart | Sessions, checkpoints, devices, handoff, transcript, cards, reviews and audit survive |
| Two API hosts write concurrently | Checkpoint numbering, review history and tool claim serialization |
| Container deployment | Real PostgreSQL 17, API restart, published companion, backup/restore |

Browser scenarios run in Chromium desktop and a Pixel-sized mobile viewport. They use
the real API for sessions, checkpoints, cards, devices and tool preparation/rejection.
The microphone scenario uses Chromium's synthetic audio device; it verifies recording,
stop/upload timing and missing-provider behavior, not a physical microphone or recognition
accuracy. Mobile viewport emulation is not an actual Android handset test.

## Repeat the tests

```powershell
dotnet test
.\scripts\test-local.ps1 -Browser
.\scripts\test-deployment.ps1 -LiveTransit
```

`dotnet test` skips real database tests unless `JARVIS_TEST_POSTGRES` is set. An invalid
configured database fails those tests rather than skipping them. `test-local.ps1` creates
an isolated native PostgreSQL cluster; use `-PostgresBin` and `-Port` as needed. Alternatively,
`run-milestone2.ps1 -Test` runs all backend tests against Docker PostgreSQL.

## Live checks on your configured installation

```powershell
$env:Jarvis__ApiKey = '<your JARVIS key>'
.\scripts\live-check.ps1
# Optional paid model request after setting server-side OpenAI credentials:
.\scripts\live-check.ps1 -Conversation
```

The script checks liveness, database readiness, live TfL and configured calendar sources.
Missing calendar configuration is reported separately. The optional conversation check
creates a labelled test session, sends one short prompt, and ends that session.

## Physical acceptance checklist

These checks require your accounts, devices and observation. They are not marked passed
by a simulated HTTP provider, a successful token configuration, or a desktop screenshot.

- On each actual laptop/phone/headset/satellite client, test microphone and audible output.
- Say a short English sentence and a French sentence; verify the cloud transcript before sending.
- Check that recorded audio and playback stop when you press Stop, hide the page, or disconnect.
- With two physical devices, complete a handoff and confirm only the current owner listens/speaks.
- Disconnect the target midway; check timeout and recovery while retaining the checkpoint.
- Confirm a permitted test lamp action and observe the real lamp. Reject another action and
  verify that the lamp does not change. Avoid testing an uncertain action by blindly retrying it.
- Compare calendar entries with the provider app, including an all-day event and a recurring event.
- Restart JARVIS and repeat a saved conversation/card review.
- On a restored test database, inspect the same saved session, checkpoints and learning history.

No OpenAI, Home Assistant or calendar credentials were configured in JARVIS's local user
secrets during this implementation. Those account-dependent checks and physical hardware
checks remain outstanding. Public TfL is testable without those credentials.

### Voice coaching validation

Use `./scripts/test-backend.ps1 -Filter 'FullyQualifiedName~VoiceCoachingTests|FullyQualifiedName~Voice_coaching|FullyQualifiedName~CoachingTests|FullyQualifiedName~PrivacyTests'` with the existing isolated PostgreSQL test setup. The PostgreSQL voice coaching test restarts hosts and races submissions across two instances, verifying one canonical attempt and matching completed lifecycle metadata.

For constrained hosts, the optional `-Diagnostics` flag adds per-test console output, limits xUnit scheduling to two parallel threads and uses a five-minute hang timeout without memory dumps. Explicit concurrency inside a test remains enabled. This option does not alter application settings or spend limits.
