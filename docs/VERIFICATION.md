# Verification results

## Native Android goals — 0.9.0, 2026-11-14

- Added Today → Your goals and More → Goals, with encrypted local drafts, workflow/status/date controls, explicit cloud consent, confirmed revision-checked deletion, conflict comparison and lost-acknowledgement recovery. Unconfirmed writes persist their exact ID/body before dispatch, lock fields and require explicit check/retry. Goals never enter the automatic/offline practice replay queue.
- Date handling retains the draft's zone, preserves existing precise instants, rejects new DST gaps/overlaps and checks deadlines/field limits. Refresh updates conflict comparison without replacing the draft. The full bounded goal catalog has a 3 MB download limit, including escaped text, while other API limits stay unchanged.
- Release build, all **48 JVM tests**, lint (zero errors; 13 existing warnings) and APK signature verification passed. Fifteen goal cases cover request identity, cloud consent, validation, time zones/DST, exact timestamps, serialization, lost create/update acknowledgements, missing/deleted records, conflict refresh and escaped-text size bounds. **89 generated Kotlin contracts** match Core; whitespace checks passed.
- **Five focused Core planning tests passed**, including two new HTTP cases for create replay, stale update/delete retries, tombstones and paused cloud-only consent. The lifecycle case also uses Android's Gson-style JSON with an omitted null `expectedRevision` and ISO UTC timestamps. Evidence: `tests/Jarvis.Tests/TestResults/android-goals.trx`. These tests use isolated in-memory data. Core application/migration code is unchanged; no production deployment or database migration was needed.
- APK `../artifacts/JARVIS-Android-0.9.0.apk`, SHA256 `99A0EB021B9B3CB99DC1DBD5C1F8B5DB495291364BDD0EEE5FA6DDAAA5A6170E`, installed successfully on Samsung SM-S928B. Package manager confirms versionCode 10/versionName 0.9.0. Core readiness, authenticated goals GET with privacy header and a fresh Android heartbeat after the APK build passed.
- On-device read-only navigation opened More → Goals and exposed the goal heading, Refresh control and Programme/agents switch. The foreground changed during scrolling; interaction stopped, the JARVIS process remained running, and its scoped AndroidRuntime log showed no crash. No screenshot was taken; the temporary UI hierarchy was removed. Real owner goals were not created/deleted, and no microphone capture or paid call was initiated.
- Full physical create/edit/conflict/restart, Keystore recovery, large-font/TalkBack and live paid-workflow acceptance remain pending. See [Android goals scope and recovery](ANDROID_GOALS.md).

## Android audio controls — 0.8.0, 2026-11-14

- Audio permission requests now report denial/recovery explicitly and Settings opens Android app permissions. Pending starts have a Cancel control. Permission results and delayed Core claims are tied to a start generation, invalidated by Stop, backgrounding or activity destruction; they cannot start capture after cancellation. The pending-start UI flag is not persisted in the practice cache.
- Answer recording now exposes a media session: Pause/toggle finishes locally for review; Stop discards. Live voice consumes only initial key-down events, with Pause/Stop ending capture and Next interrupting a reply. Play never starts capture. Repeated Finish/Discard callbacks cannot replace the first recording outcome, and a failed stop reports that no usable audio was captured.
- Release build, all **33 JVM tests**, lint (zero errors; 13 warnings) and APK signature verification passed. Eleven new audio cases cover permission denial/mode preservation, late results/claims, cancellation, recreation, key-up/repeat suppression, Play/inactive sessions and duplicate stop/finish outcomes. The extra lint warning is a `String.toUri` KTX style recommendation for the app-settings link; no dependency versions changed. **87 generated Kotlin contracts** match Core; whitespace checks passed. Core application code was unchanged by this increment, so backend/browser suites were not rerun.
- APK `../artifacts/JARVIS-Android-0.8.0.apk`, SHA256 `E5E44B9D9AA24CFE334240F06ADD44FEF95EC56F8771BFCF879B2598EA318B37`, installed successfully on Samsung SM-S928B. Package manager confirms versionCode 9/versionName 0.8.0. Core readiness passed and the Android registration received a fresh authenticated heartbeat after this APK was built.
- The phone's sleep/lock surface prevented final 0.8.0 Settings-screen acceptance. Physical headset routing, microphone, notification actions, background/call interruptions and screen-off behavior remain unverified. No microphone capture or paid provider call was started for this verification.

## Android cache privacy — 0.7.0, 2026-11-14

- Android verifies Core privacy before restoring cached practice and binds requests to the verified epoch. Observed erasure, changed generations and revoked authentication invalidate the client and clear the affected cache/queued saves. Saved credentials are cleared only when they belong to that connection; a mistyped new key preserves a separately saved connection. Late responses are rejected, and active capture/playback is stopped on revocation.
- All **167 backend tests passed**, zero failures/skips, including real PostgreSQL cases. New HTTP coverage rejects malformed/stale epochs before a session write and verifies privacy-status access after erasure. Evidence: `tests/Jarvis.Tests/TestResults/android-privacy-full-167.trx`.
- Android release build, all **22 JVM tests**, lint (zero errors; 12 existing warnings) and APK signature verification passed. Eight privacy cases cover generation changes, erasure, missing headers, late responses/disconnects, legacy cache migration, serialization and connection-identity matching. **87 generated Kotlin contracts** match Core. The final compiled classes include the playback-revocation and connection-identity safeguards.
- APK `../artifacts/JARVIS-Android-0.7.0.apk`, SHA256 `AF421BD7235B05012A12720D48EF0D45369324C89633A561CAED138CC8F98FF1`, installed successfully over 0.6.1 on Samsung SM-S928B; package manager confirms versionCode 8/versionName 0.7.0.
- Backed up production before deployment: `backups/jarvis-20261114-022741-3c3c69e1.dump`, SHA256 `B189954ADC24CD5C95DE3D396888FA42CAB5891DA1485F2A3AFE71CB7A2793F4`. Deployed image `sha256:6e01c3e4d6df14dcb8460d6a245306a7b9bc9e74450234820de0e7bbff3867e4` is healthy. Live read-only checks passed epoch headers, stale-generation 409, accepted current-generation reads, database readiness and private-CA HTTPS. Evidence: `logs/android-privacy-deployment.json`. Production erasure was not exercised.
- Initial 0.7.0 screen acceptance was blocked by the sleep/lock surface. A subsequent foreground check confirmed the connected Today and Practice navigation before starting the next Android increment. Fully offline propagation, physical erasure/restart recovery, Bluetooth/screen-off acceptance and cleanup of abandoned cache identities remain outside this result. See [privacy scope](PRIVACY.md).

## Android pairing fix — 0.6.1, 2026-11-14

- A successful connection consumed the pairing offer but left `pairingOffered` true in screen state. This kept the pairing UI visible and made further Connect taps no-ops. The connection transition now clears the offer while preserving practice drafts/queued saves.
- Release build, all **14 JVM tests**, lint and APK signature verification passed. The added transition regression verifies offer dismissal and draft preservation. APK SHA256: `8F2AFC7ABDDCF608E5469AFA7C1EF410511636950EEE752B5525FB699FC52735`.
- Installed 0.6.1 over 0.6.0 on Samsung SM-S928B. Delivered pairing over authenticated ADB, tapped the observed Connect control and verified pairing disappeared into the practice screen. Today navigation then exposed Quick practice, Story Bank and Your progress. Core also reported the Android device online.
- This verifies connection/navigation, not microphone, Bluetooth, screen-off, offline recovery or paid-provider acceptance. No screenshot was taken for this check.

## Mobile upgrade — 2026-11-14

- Android 0.6.0 release build, unit-test task, lint and APK signature verification passed (Gradle reused existing test/build outputs). All 86 generated Android contracts match Core. APK SHA256 remains `2FB9787EBE4F49A9710B2F6CDF17CB539DB9A031554B1BD4B7E8A66C0452342B`.
- Installed successfully over 0.4.0 on Samsung SM-S928B through authenticated wireless ADB; package manager confirms versionCode 6/versionName 0.6.0. The private HTTPS reverse tunnel and pairing offer were delivered.
- The lock/notification surface and a subsequent foreground change prevented app-screen acceptance. Unlock/connection, microphone, Bluetooth, screen-off and offline recovery checks remain pending. A capture made during the foreground change was removed from both PC and phone; no screenshot artifact is retained.

## Archive request ordering — 2026-11-14

- Archive details now reject delayed responses after source deletion or a newer selection. Completed claim/AI requests refresh details only while their original view remains selected.
- All **166 backend tests passed, zero failures/skips**, using an isolated PostgreSQL 16 cluster. The cluster was stopped after verification; production data was not used. Evidence: `tests/Jarvis.Tests/TestResults/all-scenarios.trx`.
- All **8 targeted desktop/mobile browser checks passed**, covering delayed detail responses after deletion, delayed assessments after changing sources, goal updates and CV claim deletion.
- The full browser suite then passed **44 tests** across desktop and mobile layouts, including session handoff, guided drills, coaching and privacy cleanup.
- Local deployment completed after backup `backups/jarvis-20261114-014616-bdfa319e.dump` (SHA256 `42D2C174D078EDEEA4B86EAF9EE7292479D15AF7802FB5B52EEDB2A0B9530F30`). API and PostgreSQL are healthy; authenticated module/import/goal/privacy reads, anonymous 401, published archive guards and private-CA HTTPS readiness passed. HTTP/HTTPS/database ports remain bound to loopback. Image: `sha256:1e9d11ed9f074d73d546a21e7013569743ebc6d49d9b511686730e720b5213b8`.
- The packaged browser modules match `JARVIS.FRONTEND`; whitespace checks passed. Live paid-provider and physical-device acceptance were not exercised.

## Guided drills — Android 0.6.0

- All 147 backend tests passed with zero failures/skips, including real PostgreSQL cross-host stage contention and restart/resume. Thirteen initial drill cases cover all nine workflows, valid stage order, retry-stable mixed selection, cancellation and due-review eligibility. AI cases also verify saved code context and deletion/cache privacy.
- All 32 desktop/mobile browser tests passed, including sequential code-review stages, saved progress, literal-text safety, mobile code layout and preservation of a local draft when another device advances the plan.
- Kotlin DTO generation produced 86 matching Core contracts. Nine modes are implemented as initial authored workflows, not a complete technical corpus or code-execution sandbox. See `GUIDED_DRILLS.md` for coverage.
- Android 0.6.0 release build, all 13 JVM tests, lint (zero errors; 12 existing warnings), and APK signature verification passed. SHA256: `2FB9787EBE4F49A9710B2F6CDF17CB539DB9A031554B1BD4B7E8A66C0452342B`. ADB currently lists no phone, so this APK has not been installed or physically tested.
- The bounded plan-history response was additionally verified against PostgreSQL: progress IDs remain available while answer bodies are omitted from session lists and loaded through the full drill endpoint. Full-suite evidence is retained as `tests/Jarvis.Tests/TestResults/guided-full-147.trx`.
- Before deployment, the existing database was backed up to `backups/jarvis-20261113-211449-47b3c099.dump`. The new .NET 11 image was deployed using the root `appsettings.json` configuration.
- An earlier concurrent test/build run had three PostgreSQL test-database creation timeouts before application setup. A serial rerun passed all 147 tests without changing timeout limits. Final verification is run sequentially to avoid host resource contention.

## Offline practice — Android 0.5.0

The Android increment adds an authenticated encrypted no-backup cache, debounced drafts and an explicit-retry queue for unpaid practice writes. Eight new JVM tests cover restart/lost-acknowledgement identity, changed inputs, conflict persistence, route exclusions, capacity and authenticated-encryption behavior. Physical airplane-mode/process-death/Keystore recovery acceptance remains pending. See `OFFLINE_PRACTICE.md` for the exact scope and limits.

- Final 0.5.0 release build and all 12 Android unit tests passed. Lint completed with zero errors and 12 warnings (existing dependency, preference, icon and certificate-pin warnings). APK signature verification passed.
- APK SHA256: `67DA74A57AD638DAF54DAD076BBACC66E287970449BFFBEDC78361D7ACC2A728`.
- The Android module-catalog regression passed; current Kotlin DTOs match the 80 generated Core contracts. Module status was deployed to the .NET 11 server and private HTTPS/database readiness passed. Backend configuration remains the root `appsettings.json`.
- 0.5.0 was not installed: ADB discovery returned no phone and the previous wireless endpoint timed out. Installation and physical recovery checks require reconnecting the phone.

## Realtime / AI / agents — Android 0.4.0, .NET 11

- 28 desktop/mobile browser tests passed, including consented AI-feedback rendering and device handoff. The handoff test now refreshes the device list before confirming hardware verification; microphone testing is disabled until registration completes, and the verification control survives device-list refreshes.
- 131 backend tests passed with no failures or skips, including real PostgreSQL tests. New cases exercise expiring audio fences, one-time tickets, in-process WebSocket authentication/configuration/transcript/usage handling, assessment consent/evidence/replay/correction races, specialist claims, source-change rejection, schedules, quiet hours, outbox deduplication and cross-lane budgets. Provider responses in these cases are fixtures.
- Isolated .NET 11 container deployment passed startup, database readiness, published browser assets, canonical writes, API restart survival, binary backup and restore into a separate database. Evidence: `TestResults/jarvis-verify-61aae4da2e`.
- Android 0.4.0 release build, four unit tests, lint and APK signature verification passed. The APK was installed over the existing authenticated ADB connection without launching the app. SHA256: `91B87098814BCE658648BDAF698FC05BD9DD4E74EBAB33B2AFBD49AB8B8CA72E`.
- Production configuration now loads only the private root `appsettings.json`; the old source-level appsettings files were removed. Docker images contain sanitized defaults and mount private settings at runtime.
- Live paid-provider quality, phone microphone/playback, Bluetooth, screen-off/battery and long-running recovery acceptance remain unverified. Native offline queue, full nine-mode technical trainer, CV/archive import, retention jobs, identity isolation, Windows agent and Quest/satellite clients remain incomplete. This is not full master-requirements acceptance.

The earlier results below describe historical versions.

## Multi-module coaching and clients — Android 0.3.0

- **118 backend tests passed, zero failures/skips**, including PostgreSQL cross-host
  save deduplication, correction contention, restart survival and programme check-ins.
  Evidence: `tests/Jarvis.Tests/TestResults/modules.trx`.
- **26 browser tests passed** across desktop/mobile viewports: existing scenarios plus
  coaching save/correction/review, real-story CV defence, literal HTML/XSS handling,
  technical self ratings, programme check-ins, sign-out clearing, disabled paid work
  and an actual committed/lost-response retry producing only one answer.
- Browser testing found and fixed explicit label association and loss of the technical
  editor value while creating its first canonical session. Final full suite passed.
- Kotlin release build, **four Android tests**, lint and v2 APK signature verification
  passed. **61 generated contract types** matched C#; the packaged browser bundle matched
  its `JARVIS.FRONTEND` source. `git diff --check` passed.
- Browser coaching screenshots were inspected at desktop/mobile test sizes; mobile width
  overflow checks passed. Synthetic browser audio tests do not prove native hardware behavior.
- APK: `../artifacts/JARVIS-Android-0.3.0.apk`, **20,284,784 bytes**.
  SHA-256: `6109E131FD310FBCADC6B686AA01165B625675490E0768F25238EAD3B4E161A8`.
- Installed in the background on Samsung SM-S928B/Android 17. Package manager confirmed
  versionCode 3/versionName 0.3.0. The current phone screen was not opened/navigated;
  native visual interaction and a fresh 0.3.0 connection check remain pending.
- Backup before deployment: `backups/jarvis-20261113-120521-a7034267.dump`.
  Production API/PostgreSQL became healthy. Authenticated live checks verified 15 module
  records, 24 exercises, eight communication exercises, twelve programme weeks and a
  resolved coaching prompt. Anonymous access returned 401; paidReady remained false.
  No personal programme or synthetic live coaching answers were created by these checks.
  Evidence: `logs/modules-verification.json`.
- Full realtime voice, native coaching visual/interaction acceptance, calibrated automatic
  scoring, archive, research/career specialists and Quest/room clients remain pending.
  See [implementation coverage](IMPLEMENTATION_STATUS.md).

## Technical training — Android 0.2.0, 13 November 2026

- Backend suite: **101 passed, zero failed/skipped**, including real PostgreSQL 17
  cross-host training contention and restart persistence. Evidence:
  `tests/Jarvis.Tests/TestResults/training.trx`.
- Browser regressions: **14 passed** across desktop and mobile viewports.
- Training tests cover evidence/rationale validation, unknown metrics, skips,
  concurrent revisions, conflicting retries, deletion tombstones, bounded histories,
  correction replay, existing/later manual reviews, same-instant event ordering,
  1/3/7/14-day schedules and DST/calendar behavior.
- Kotlin contracts matched all **42** generated types. No EF schema migration is needed.
- The first Docker-database run encountered connection timeouts and was stopped.
  Final runs passed with `GSS Encryption Mode=Disable` on the local password-authenticated
  test connection, avoiding irrelevant Kerberos negotiation. Tests use disposable
  `jarvis_test_<guid>` databases and do not run against the personal application database.
- Pre-deployment backup: `backups/jarvis-20261113-103218-9beb0fa2.dump`.
- Training scope and limits: [TRAINING.md](TRAINING.md).
- Final native release build, four Android unit tests, lint (zero errors) and APK
  signature verification passed. Build script now explicitly sets workspace-local
  ANDROID_USER_HOME, avoiding Android's unwritable fallback settings directory.
- Signed APK `../artifacts/JARVIS-Android-0.2.0.apk`, **20,120,944 bytes**;
  SHA-256 `8152001171E1B5E45C707C856896AF588A222C540D318692EAF76C2A92638A49`.
- Installed over the existing app on Samsung SM-S928B; package manager confirmed
  versionCode 2/versionName 0.2.0 and successful cold launch. Core observed an authenticated
  Android registration/heartbeat after upgrade using the saved profile.
- Final screen inspection was stopped because another app became foreground. No unrelated
  screen capture was taken. Native training visual/interaction acceptance remains pending.
- Production API/PostgreSQL are healthy. Live checks returned 16 questions/eight domains,
  valid dashboard and bounded learning-card responses, 401 for anonymous access and
  paidReady=false. HTTPS readiness passed with the supplied certificate. Evidence:
  `logs/training-verification.json` and `../artifacts/android-verification.json`.

## Native Android and paid AI — 13 November 2026

- Final backend suite: **82 passed, zero failed/skipped** with real PostgreSQL 17.
  Evidence: `tests/Jarvis.Tests/TestResults/android-paid-ai.trx`.
- Browser regressions: **14 passed** across desktop/mobile viewport projects.
- Kotlin release APK compiled and signed; `apksigner verify` passed (v2 signature).
  Android unit tests: **4 passed** (HTTPS address and certificate-pin validation).
  `lintRelease` passed with no errors; advisory dependency/style warnings remain.
- Generated Kotlin contracts matched the C# generator; build/pairing scripts passed
  PowerShell parsing. Optional JSON fields required no new EF model migration.
- Installed and launched on Samsung **SM-S928B, Android 17/API 37** through authenticated
  wireless ADB. Reinstallation preserved encrypted connection credentials.
- Native app connected through the certificate-pinned HTTPS reverse tunnel, registered
  itself and was observed **online** with foreground heartbeats.
- Updated UI includes one-tap pairing, expandable manual setup, Today action cards, focus
  chips, retained in-process drafts, story editing and workflow controls. Complete visual
  and accessibility acceptance is not claimed by compilation or UI hierarchy checks.
- Private HTTPS listener verified with its supplied certificate, without `--insecure`
  or OS trust-store modification. It binds only to loopback port 5186.
- Paid workflow tests cover context/source validation, atomic reservations, known/unknown
  usage, cross-host budget races, unexpected-charge shutdown, deletion tombstones and
  suppression of replay after an uncertain attempt.
- Production Core deployed after backup. Live check confirmed PostgreSQL readiness and
  **503** for unconfigured paid requests. No real Claude call or API charge was generated.
- Backup: `backups/jarvis-20261113-042301-4fd832a7.dump`. Live evidence:
  `logs/android-paid-ai-verification.json` (both Git-ignored).
- APK: `../artifacts/JARVIS-Android-0.1.0.apk`.
  SHA-256: `DDA3DEADBC923D1B3B841A6B474B71E02A5A7E88CADD2D91063BD3905A1A047E`.
- Real keys remain absent/disabled. Actual cloud quality, microphone/earbud/locked-screen
  acceptance, interruptions, battery, full duplex, OIDC, offline operation and complete
  retention/deletion acceptance remain pending.

## Durable practice workflows — 13 November 2026

- Build: zero warnings/errors. Additive EF migration `20261113031234_WorkflowRuns`.
- EF `has-pending-model-changes`: no drift after migration generation.
- Initial full suite on isolated PostgreSQL 16: 70 passed. Final suite including two
  additional lease/failure checks on PostgreSQL 17: **72 passed, zero failed/skipped**.
  Evidence: `tests/Jarvis.Tests/TestResults/workflow-foundation.trx`.
- Browser regression: **14 passed**, desktop and mobile viewport emulation; synthetic
  microphone tests are not native Android/hardware evidence.
- New tests cover trigger deduplication, conflicting retries, exclusive claims, token
  fencing, exact lease expiry, cancellation, heartbeat renewal, transient/permanent failure,
  retry/crash limits, deadlines, deterministic source selection, HTTP protection and worker execution.
- Real PostgreSQL tests verify two-host trigger/claim contention, post-restart recovery,
  stale worker rejection, event transactionality and `SKIP LOCKED` nonblocking claims.
- Backed up the existing server database before deployment:
  `backups/jarvis-20261113-032517-8b787a4b.dump` (Git-ignored; SHA-256 recorded by backup script).
- Built and deployed the Production Compose API with automatic migration. Live smoke
  verified readiness, enqueue/completion, same-ID retry, three ordered events, queued
  cancellation, anonymous access rejection and exclusion of internal worker metadata.
- Restarted the API; completed result, event revisions and cancelled state survived.
  Evidence: `logs/workflow-server-verification.json`. Two synthetic run records remain
  as diagnostic history; no story, answer or CV content was added by these checks.
- Server remains running on `http://localhost:5185` with PostgreSQL 17.
- Paid providers, calendar-triggered AI briefs, Android APK/native audio and physical
  device acceptance were not enabled or claimed. See the integration ADR for remaining gates.

## Local server deployment — 13 November 2026

- Full backend suite rerun with isolated PostgreSQL 16: 58 passed, zero failures/skips.
- Desktop/mobile Playwright suite rerun: 14 passed (`playwright-report/index.html`).
- Built and started the Production Docker Compose API and PostgreSQL 17 on this machine.
- Companion at `http://localhost:5185`; `/health/ready` reports Postgres ready.
- Confirmed anonymous API access returns 401 and authenticated story creation succeeds.
- Restarted both containers, verified the saved story survived, then deleted only that
  synthetic story. The containers remain running with persistent storage.
- Local smoke evidence: Git-ignored `logs/server-verification.json`.
- OpenAI, transcription, Home Assistant and calendar credentials remain unconfigured.
- See [server operation](SERVER.md) for connection credentials, restart and backup commands.

## Story Bank backend — 13 November 2026

- `dotnet build Jarvis.sln --no-restore --verbosity minimal`: passed, zero warnings/errors.
- `dotnet test Jarvis.sln --verbosity minimal`: 50 passed, eight database tests skipped.
- `scripts/test-local.ps1 -PostgresBin 'C:/Program Files/PostgreSQL/16/bin'`:
  58 passed, zero failures/skips. Evidence: `tests/Jarvis.Tests/TestResults/all-scenarios.trx`.
- Story tests cover draft creation, duplicate requests, field validation, authentication,
  read/update/delete, stale writes/deletes, concurrent edits, legacy JSON compatibility,
  real PostgreSQL restart persistence and contention across two hosts.
- Existing CQRS architecture checks include the new story commands, queries and registration.
- Stories extend the existing JSON runtime document; no EF model change or migration.
- The isolated PostgreSQL test server was stopped; logs remain in the temporary cluster directory.
- Browser and deployment suites were not rerun: this change adds backend endpoints only.

The results below are historical verification from 30 October 2026.

## CQRS refactor verification

- Build: 0 warnings, 0 errors.
- `scripts/run-milestone2.ps1 -Test`: 53 passed, 0 failed, 0 skipped, including real
  PostgreSQL 17 tests and four new CQRS architecture/dispatch tests.
- `npm run test:browser`: 14 passed, including desktop/mobile and two-browser handoff.
- Migration check: no model changes; no migration required.
- Application-owned read/provider ports keep endpoints free of persistence and concrete
  provider dependencies. Commands and queries retain the same HTTP contract.

The earlier implementation/deployment checks below are historical evidence. The container
packaging and backup/restore suite was not rerun for this in-process architecture refactor.

## Original milestone implementation verification

| Check | Result | Evidence |
|---|---|---|
| .NET build | Passed, 0 warnings, 0 errors | `dotnet build --no-restore` |
| Backend suite | 49 passed, 0 failed, 0 skipped | `tests/Jarvis.Tests/TestResults/all-scenarios.trx` |
| Database scenarios | Included above; real PostgreSQL 18 on Windows | Restart, upgrade, concurrency, durable runtime state |
| Browser suite | 14 passed | `playwright-report/index.html` |
| Desktop/mobile workflows | Passed in Chromium and Pixel viewport emulation | Sessions, learning, tool rejection, devices, synthetic recording, two-browser handoff |
| Packaged deployment | 6 checks passed; real PostgreSQL 17 in Docker | `TestResults/jarvis-verify-87c15da067/deployment-report.json` |
| Restart and backup/restore | Passed in real containers | Saved session/checkpoint/review survived restart and restored into another database |
| Public live integration | TfL passed directly and through the containerized API | Live Jubilee line response; deployment report |
| Migration drift | None | `dotnet ef migrations has-pending-model-changes` |
| Package audits | No reported vulnerabilities | NuGet audit including transitive packages; npm audit |
| PowerShell scripts | Syntax checks passed | Parser validation; deployment/backup/restore scripts also executed |

The temporary native PostgreSQL server was stopped. Verification Compose containers,
networks and test volumes were removed. Test reports, screenshots, logs and synthetic-data
backup files are retained in Git-ignored folders. No production service or data volume was
replaced. The pre-existing `.claude/settings.local.json` modification was left untouched.

## Not verified live

OpenAI conversation/transcription, Home Assistant physical actions and Google/Microsoft
calendar access had no configured credentials in JARVIS user secrets. Their adapters were
tested with simulated provider responses. Browser audio tests used synthetic microphone
input. Actual microphone acoustics, audible playback, real Android/Quest/satellite hardware,
and account-specific calendar contents remain to be verified using `TESTING.md`.

This build includes a browser companion, not native mobile/headset/satellite clients.
Calendar account login and automatic token refresh are not implemented. Wake-word mode
is foreground browser speech recognition, not an offline or always-on background detector.

## Core privacy verification — 2026-11-13

Android development is deferred. Core remains on .NET 11 with backend configuration in root `appsettings.json`.

- Full backend run: **155 passed, zero failed/skipped**, including real PostgreSQL integration tests. Evidence: `tests/Jarvis.Tests/TestResults/privacy-full-155.trx`.
- Browser suite: **34 passed** across desktop and phone-sized layouts. Added focused privacy run: **4 passed**, including a delayed pre-erasure response rejected after browser cleanup.
- Coverage includes owner-data export without credentials/claims, typed erasure confirmation, rotated-key reopen, stale request fences, cross-host/restart persistence, retention cascades/idempotency, recent-correction preservation, mirrored voice removal, and numeric charge settlement without private-result resurrection.
- Automatic retention defaults to disabled. Destructive acceptance checks use disposable databases. No live user data is erased for verification.

See [privacy controls and limits](PRIVACY.md). Provider-held data, backups, downloaded exports and offline device caches require separate lifecycle management.

Deployment acceptance passed in disposable project `jarvis-verify-9212a5d184` (report: `TestResults/jarvis-verify-9212a5d184/deployment-report.json`). Checks included container readiness, published UI, durable sessions/checkpoints/learning, actual container restart, binary backup restore to another database, owner export, atomic erasure retry and erased-state maintenance gate.

Live pre-deployment backup: `backups/jarvis-20261113-224453-2dfc65c6.dump`, SHA256 `38E0F1748BE13A0759013D9B3ABAA0A094A5E73E34053FBCF3258D3F0D81C4F3`. The Core privacy migration and browser controls were deployed after that backup. Private-CA HTTPS readiness passed against PostgreSQL. Live destructive privacy operations were not exercised.

## Audio-only Android 0.10.0 — 14 November 2026

Owner direction supersedes native Android feature screens. Android now provides audio transport and connection/start/stop controls. Core owns idempotent conversation preparation/resumption and bounded saved practice context. Display transcript events are suppressed for registered audio-only devices. Old rich-client source remains outside the build; existing Core data/APIs remain intact.

Validation: signed release build and signature verification passed; 17 Android JVM tests passed; lint completed with 0 errors and 13 warnings. APK DEX scan excluded legacy feature screen/service classes. All 13 generated contracts match C# source. Backend full run: 171 passed, 4 database setup timeouts; the same 4 tests passed on a focused retry after Android finished (175 distinct tests validated, no skips). No live provider calls were used.

APK SHA256: `27AB2653C5E778D160FACAF784991ACEE21774EAE45A2B11FF12A91A5EB41867`. Installed and paired on Samsung SM-S901E at the explicitly authorized ADB target. Observed the audio-only home screen after accepting its connection offer. Microphone and earbuds remain unverified; provider readiness reports OpenAI API key unconfigured. Persistent away-from-home HTTPS/VPN and full voice-driven business tool actions remain incomplete.

Predeployment database backup: `backups/jarvis-20261114-122755-a3676d9d.dump`, SHA256 `389194FBA8E696AFCEA74C4A9A187BCEDA820E0B40EE34C04DC048731A2E3B86`.

Deployment completed: Core image `sha256:f1f06fd9368d12d1fb602069485c04ba0c798775f19fd848b1199cf4d296e111` healthy; `/health` HTTP 200. A consent-false/empty-ID deployment probe to `/api/voice/prepare` returned HTTP 503 (provider unconfigured), confirming the deployed route without a conversation or provider call. Android package versionCode 11 was observed. Scoped AndroidRuntime log check produced no crash entries. No microphone start or owner hardware verification was performed.

## Core realtime tools — 14 November 2026

Implemented fixed Core voice context/practice tools, canonical transcript-backed technical answers, persisted drill resume, retry identity, completed-only provider dispatch, duplicate-response suppression, bounded tool batches/results and current session-owner fencing. Android remains the installed audio-only 0.10.0 transport with 13 unchanged generated contracts.

Validation: full backend suite 184 passed, 0 failed/skipped, including a real PostgreSQL restart/multi-host voice-practice test. It starts a saved deep-dive drill, restarts Core hosts, submits the same utterance concurrently from six callers, and verifies one canonical answer and one-stage advancement after another restart. The full result is retained at `tests/Jarvis.Tests/TestResults/core-voice-full.trx`. After final protocol/result-envelope changes, 11 affected voice/service tests passed again. These tests use provider fixtures, never paid calls. Contract regeneration matched all 13 Android types; diff whitespace check completed after documentation cleanup.

Provider fixture coverage includes user-transcript reference injection, successful start/answer dispatch, duplicate terminal events, cancelled function calls without mutation, canonical answer persistence and exact usage settlement. Unit coverage rejects generated/nonlatest/out-of-window answer sources and arbitrary actions/extra fields, preserves cancelled drill history, excludes goals without cloud consent and hides due-card references before an attempt. Audio lease checks immediately reject a changed/paused session owner.

Predeployment backup: `backups/jarvis-20261114-125346-197b462f.dump`, SHA256 `D1D20331EBBACFDCCAC99F5FCEC1DD23D9229D94898DDAA614A9E6F4F982DB08`.

Remaining: other voice business workflows, adaptive/calibrated coaching, configured-provider/hardware/WAN acceptance and the prioritized items in CORE_BACKLOG.md. Production provider configuration is still missing; this increment does not claim all Core requirements are finished.

Deployed Core image `sha256:a1ce8b1f82d2c4f11316315363ca220169c838fd54ae2dd89cab8b37fd679c56`; container healthy and `/health` HTTP 200. Authenticated `/api/modules` reports the new Core voice tool inventory. `/api/voice/status` still reports the OpenAI API key unconfigured. No production session/practice record or paid provider call was created by deployment checks.

## Core voice recall — 14 November 2026

Implemented persisted due-card recall phases and the fixed `core_recall` tool. The existing learning scheduler commits a voice rating and recall completion atomically. References remain hidden until a separate captured attempt; a separate explicit Again/Hard/Good/Easy utterance must match the proposed rating. Ambiguous/negated utterances, wrong ratings and stale externally reviewed cards are rejected. Retry/concurrent rating delivery schedules once. Timing is explicitly `not_measured`; the legacy duration slot is not a measured zero. Long Unicode question/reference content remains bounded and marked incomplete. Recall metadata stores source IDs only, is included in privacy export, and is removed by source/session retention and erasure.

Validation: full backend suite **193 passed, 0 failed, 0 skipped**; result retained in `tests/Jarvis.Tests/TestResults/core-recall-full.trx`. The 20 targeted voice/privacy/PostgreSQL tests passed before the final full run. New coverage includes phase/reference gates, explicit-rating matching, concurrent/replayed rating identity, changed-card conflicts, tool-window scoping, export/retention/erasure metadata cleanup and long Unicode output bounds. A real PostgreSQL test resumes a revealed card after host restart, rates it concurrently from six callers across two hosts, and verifies one canonical review after another restart. No paid provider or production practice calls were used.

All 13 generated Android contracts match; Android 0.10.0 remains unchanged. Core changes are additive JSON/runtime fields, with no relational migration. Source whitespace check passed. Predeployment backup: `backups/jarvis-20261114-134417-008259d0.dump`, SHA256 `3F91450C36A8E6CD84E58106BD07F6741DA1611A44F143A1676CB3AEEA6BE1E7`.

Deployment completed with image `sha256:071b1c76ed0497352ca712d74c8262aed7c0eec9d8910ded373ebb2bda147195`. Container healthy, `/health` HTTP 200, authenticated module inventory confirms spoken due-card review. Voice readiness still reports OpenAI API key unconfigured. Production smoke checks were read-only and did not create practice data or invoke paid providers. Earbud/provider/WAN acceptance remains pending.

## Approved Slices 0/1 — central GBP authority (source implementation)

Owner authorized building the reviewed Slice 0/1 plan. Existing projects, domain contracts/IDs and migration history remain intact; Android stays audio-only. No provider credentials, private root settings, actual FX/tax assumptions or production deployment were changed.

Slice 0 baseline: backend 193/193 passed with real PostgreSQL enabled (`TestResults/gbp-baseline.trx`); browser 44/44 passed. Android release JVM/lint invocation completed successfully: JVM results were up to date (17 existing passing tests), lint task passed; no APK rebuild or phone microphone/provider test was performed. ADR 0003 records the accepted implementation scope.

Slice 1 adds central £10 development / £100 production monthly authorization, explicit expiring owner approvals up to a hard £200 production maximum, frozen GBP accounting beside original USD reservations, legacy charge preview/classification, and separate owner management. Existing USD constraints remain additional restrictions. GBP and USD reservation/settlement updates share the current runtime transaction and require no relational migration. Unknown/missing/stale policy/FX/tax settings fail closed. Standalone transcription now uses dated readiness validation too.

Budget management requires a separate owner header/credential, not a caller-supplied actor or the ordinary Core key. Existing billing reconciliation is now owner-only to prevent untrusted charge reductions from bypassing the allowance. Erasure preserves minimal accounting and scope, revokes approvals and redacts actor/evidence metadata; reopening cannot replay old approvals or reset spend. Late numeric settlement retains its original conversion basis.

New targeted tests: 18 passed, including cross-host PostgreSQL mixed-provider/lane reservations, revocation racing dispatch, overflow rollback without partial settlement, owner management API denial, stale/mismatched approvals, unknown legacy charges, original quote accounting, month rollover, erasure and late numeric settlement. The first full regression run passed 209/211; two legacy PostgreSQL fixtures lacked an explicit synthetic GBP policy. Only fixture setup was corrected. The clean final backend run passed **211/211, zero failures/skips**, retained in `TestResults/gbp-slice1-full.trx`.

All 13 Android contracts match their C# source; all nine packaged frontend modules match their edit sources. Tests use explicitly injected synthetic prices and throw-on-real-provider fixtures. Those prices are not production configuration. New example budget/management settings remain disabled/empty. No dependencies or projects were added.

## Final central-policy baseline, 14 November 2026

The final pre-coaching backend diagnostic run passed 214 tests, with zero failures or skips. The browser suite passed 44 tests. A preceding slow full-suite attempt was explicitly stopped and is not counted as passing evidence. Candidate image `jarvis-api:gbp-slice1-20261114` built successfully as `sha256:1cc45525ae3bf94828216333579c004c8b7430b1fe603e16d34836d388b2a4d4`; it predates the subsequent coaching preparation source increment and was not deployed. Running `jarvis-api-1` remained healthy on image `sha256:071b1c76ed0497352ca712d74c8262aed7c0eec9d8910ded373ebb2bda147195`.

## Voice coaching preparation validation, 14 November 2026

Final source: 32 affected backend tests passed, zero failures/skips (`VoicePracticeTests`, `CoachingTests`, `CompanionVoiceTests`). This includes five new coaching cases covering catalogue/prompt reuse without paid calls or saved attempts, missing sources/unknown exercises, and saved CV claim provenance with stale-version rejection. Results archived as `tests/Jarvis.Tests/TestResults/voice-coaching-preparation.trx`. No full-suite rerun, device acceptance or deployment is claimed for this subsequent increment. Earlier 214-test full-suite evidence applies to the preceding central-policy source only.

## Core voice coaching lifecycle — 14 November 2026

Implemented start/current/answer/stop through the existing realtime channel, preserving canonical coaching history. Transcript-backed answer creation and lifecycle completion use one runtime transaction. Exercise and saved-story versions are checked; deleted/stale answers cannot be replayed. Responses provide bounded text observations and next drills, with no measured duration, calibrated score or separate paid assessment. Privacy export includes lifecycle metadata; erasure and retention remove it.

Verification on final production source:

- 53 affected tests passed, zero failures/skips: voice coaching, voice practice, voice recall, coaching, companion preparation, privacy, plus PostgreSQL coaching restart and six concurrent submissions across two hosts. Archived `tests/Jarvis.Tests/TestResults/voice-coaching-lifecycle.trx`.
- Seven realtime protocol tests passed, zero failures/skips, including new coaching success/cancellation cases and duplicate terminal-event handling. Archived `tests/Jarvis.Tests/TestResults/voice-coaching-realtime.trx`. Only test fixtures changed between these two final runs.
- An earlier affected run had one fixture failure because its erasure key fingerprint was not a valid hash. Corrected the fixture; the final run above passed. No privacy guard was relaxed.
- Generated Android contracts match all 13 existing generated types. All nine frontend module copies match their canonical sources. Git whitespace check passed (existing line-ending notices remain).
- No dependencies, projects, database migrations, private settings, paid provider calls or Android changes were introduced. No browser-suite rerun or physical hardware acceptance is claimed for this increment.
- The existing running deployment remained healthy on image `sha256:071b1c76ed0497352ca712d74c8262aed7c0eec9d8910ded373ebb2bda147195`; this source increment is not deployed. The earlier GBP candidate image does not include these coaching changes.

Limits: answer-vs-command classification relies on realtime instructions; Core enforces canonical latest-user transcript identity, not semantic intent recognition. Full spoken correction, automatic programme sequencing, and real-provider/headset acceptance remain future work. No full backend-suite rerun is claimed for this increment; the 60 tests above cover the affected areas.

## Spoken coaching correction and feedback resume — 14 November 2026

Final source validation: 64 affected backend tests passed, zero failures/skips, covering voice coaching, technical practice, recall, companion preparation, existing coaching, privacy and realtime protocol behavior. Archived `tests/Jarvis.Tests/TestResults/voice-coaching-correction.trx`. The PostgreSQL coaching test now also restarts with a pending correction and races six replacements across two hosts, verifying exactly two canonical answer versions (original plus replacement).

New correction tests cover separate preparation/replacement speech, stable retries, original-history preservation, stale revision conflicts after text edits, cross-session target denial, live-window tool validation, deleted-answer replay denial, and stopping after deletion without returning deleted text. Current feedback includes canonical revision and resumes the latest completed voice exercise when no question/correction is pending. Privacy retention covers the correction-start utterance reference.

Android contract generation still matches all 13 types; all nine frontend module copies match; whitespace check passed. No full-suite/browser/hardware rerun is claimed for this increment. No migration, dependency installation, paid provider call, private configuration change or deployment occurred. Running Core remained healthy on image `sha256:071b1c76ed0497352ca712d74c8262aed7c0eec9d8910ded373ebb2bda147195`.

Compatibility/rollback: new correction state is additive runtime JSON metadata with optional fields. Existing canonical coaching answer and correction contracts remain intact. Pause voice work and preserve data before rollback; do not run older whole-document writers alongside writers that know the new metadata. The earlier budget-only image does not contain these voice changes. Realtime intent classification remains instruction-based; transcript identity and revision protection are enforced by Core.

## Claude independent-review remediation — 15 November 2026

Source remediation covers companion-scoped device credentials, owner step-up for privacy/trust-changing actions, device-ID binding and revocation fencing, cloud-recognition opt-in, Home Assistant TLS enforcement, recent-story selection, injected agent clocks and the first-audio metric. The detailed open/partial/closed response is in workspace `claude-review/JARVIS_CLAUDE_REVIEW_REMEDIATION.md`. The original five independent reviews are unchanged.

Fresh verification:

- Full backend rerun: **260 passed, zero failures/skips**, including real PostgreSQL restart, concurrent-host and security tests. Archived `tests/Jarvis.Tests/TestResults/claude-review-full.trx`. The preceding full run had 257 passes and two fixture failures: older custom PostgreSQL clients omitted the new owner header. Those test clients were corrected; application authorization was not relaxed.
- Browser: **44 passed** on desktop/mobile profiles (2.6 minutes), including explicit owner credentials for sensitive actions, no credential storage, clearing the owner field on disconnect and declining cloud keyword recognition before constructing its recognizer.
- A subsequent bounded erasure-marker/draft-preservation follow-up changed only the failed-device-auth erasure response on Core and Android access-failure classification. **31 final affected backend tests passed**, including privacy, device authorization and cross-host credential revocation. Archived `tests/Jarvis.Tests/TestResults/claude-review-privacy-followup.trx`. No second full-suite rerun is claimed after that final response-marker change.
- Android contracts: all 13 generated types still match. All nine frontend module copies match. JavaScript module syntax and git whitespace checks passed (existing line-ending notices remain).

No production configuration, signing key, deployed phone, existing encrypted draft file or running container was changed. No dependencies/projects/migrations were added; no paid provider calls or external home actions were made. Running Core remained healthy on image `sha256:071b1c76ed0497352ca712d74c8262aed7c0eec9d8910ded373ebb2bda147195`. These changes are not deployed. No physical microphone/earbud/WAN acceptance is claimed.

Operational limits at that earlier checkpoint: configure the separate owner credential before rollout; migrate phone credentials and rotate the old broad Core key. Export any needed legacy drafts before changing their connection identity. The subsequent entry below supersedes the earlier implementation-gap list. Source remediation is not blanket approval for WAN or paid activation.

## All-review follow-up — 15 November 2026

Implemented canonical bounded audio decoding, class-specific realtime reservations, one-use hub tickets, all-path browser audio leasing and revocation drains, narrower PostgreSQL audio reads, idle-write suppression, cache maintenance/metrics, serialized migrations, encrypted Windows backups, trusted invocation origins, partitioned rate limits and minimal readiness. Workflow extensions use an immutable catalogue and pinned run policy. Shared bounded relevance selection reaches older stories through the read-only voice search. Core handles incoming Android handoff without moving conversation logic into the phone.

Fresh results:

- **276 backend tests passed**, zero failures/skips, including PostgreSQL cross-host/restart/privacy tests. Archived `tests/Jarvis.Tests/TestResults/review-current-backend.trx`. An earlier unrestricted run was interrupted because it was slow on this memory-constrained machine; the completed bounded rerun took 5.9 minutes. `scripts/test-backend.ps1` now defaults to two xUnit threads; explicit concurrency inside tests remains enabled.
- After final catalogue discovery, snapshot retention and serialized-context-size corrections, **28 affected backend tests passed**, zero failures/skips. Archived `review-final-followup.trx`. This is the final backend source check for this increment, rather than a second full-suite claim.
- **44 browser tests passed** on desktop/mobile profiles, 2.6 minutes. Includes recording/handoff exclusion, consent, privacy, literal source rendering, canonical coaching and preserved drafts. Log `TestResults/review-browser-final.log`.
- Synthetic Linux decoder fixtures: normal and forged-byte-rate WAVs each normalize to exactly two seconds/64044 bytes. An actual 181-second decoded fixture is rejected with `DomainException: Decoded audio exceeds three minutes.` `TestResults/audio-over-limit-result.json` checks the expected rejection text as well as exit status. No paid call was made; the legacy metadata exploit itself is not claimed reproduced.
- Isolated candidate deployment acceptance passed: build, API restart, encrypted DPAPI PostgreSQL backup, restore into a different database, export/erasure and maintenance checks. `TestResults/jarvis-verify-09e5c96df7/deployment-report.json`. Temporary project containers/network/volume were removed. Candidate image `8a26f472d7fa81fc7e9e5cbb7cfc971998b349ad0770f753572848d036a44adc` predates the latest workflow/context/handoff source and is not its release image.
- Candidate SPDX inventory: **322 packages**, `TestResults/jarvis-isolated-candidate.spdx.json`. NuGet vulnerability report found no known vulnerable packages. The inventory is not an OS vulnerability scan or licence approval.
- Frontend parity (nine modules), JavaScript module syntax and PowerShell syntax (20 scripts) passed. The existing baseline includes intentional Markdown hard breaks and pre-existing blank-at-EOF whitespace warnings; authoritative requirement formatting was preserved.

Android final APK validation and local baseline hashes are recorded in the subsequent completion entry when available. Earlier Android validation runs passed 20 JVM tests and release lint, but predate the final lifecycle/version changes; they do not establish final APK acceptance.

Remaining review work: complete domain/capability registration and legacy workflow adapters (R6), provider-neutral routing with durable attempt records (R7), versioned relational Domain Registry (R8), owner-approved manifest-only Factory (R9), local wake detection/arbitration (R12), remote CI configuration, real hardware and deployment acceptance. Relevance selection is deterministic lexical retrieval, not semantic memory or a paid reasoning evaluation. No all-review closure is claimed.

Production remained healthy on `sha256:071b1c76ed0497352ca712d74c8262aed7c0eec9d8910ded373ebb2bda147195`. No new dependency/project or database migration, real paid-provider call, private settings change, key rotation, phone installation or production deployment occurred in this increment.

## Next-actions baseline and RR follow-up — 15 November 2026

Step 0 uses one baseline commit because the existing changes span shared contracts and composition: Core `0a888d5`, tagged `baseline-2026-11-15`; Android `9414eb6`; frontend `8a5978d`. Configured owner Git identity was used without overrides or attribution trailers. No remotes, pushes, amendments or history rewrites were made. Private/generated paths remain excluded.

Fresh RR verification:

- RR-01: two Android transport policy JVM tests passed (`TestResults/rr01.log`, 2m5s). A burst of ten seconds fits within the twenty-second PCM budget; overflow remains rejected. Mobile commit `b35dab5`.
- RR-02/RR-03: 36 affected backend tests passed (`TestResults/rr02-03.log`, 58s), covering companion continuity, authorization and provenance. RR-02 adds PostgreSQL metadata selection and handoff-only polling; RR-03 appends Unknown origin and tags HTTP/background entry points.
- Subsequent RR backend checks: 51 passed (`TestResults/rr-followup.log`, 1m17s), including PostgreSQL projections, device-bound stopped-audio release, existing handoff, provenance, agent and push behavior.
- Final RR-07 checks: 14 passed (`TestResults/rr07-final.log`), including an idle agent tick/claim test asserting zero calls to UpdateAsync. Read-only prechecks precede write transactions for scheduling, claims, stale-tool recovery, pruning and push delivery; mutations still recheck conditions under the existing transaction.
- RR-04: two browser tests passed on desktop/mobile (`TestResults/rr04-browser.log`, 24.3s). Ordinary GETs carry no owner credential; privacy export still carries it.

RR-05/RR-06 Android validation completed: **21 JVM tests passed, zero failures**, release lint passed (`TestResults/rr05-06-android.log`, 3m39s). Mobile commits `ff7f3e6` and `3e8f739`. The earlier APK predates these RR fixes; rebuild before hardware acceptance. No fresh hardware, remote CI or production acceptance is claimed.

Core RR commits: RR-02 `f6bbdbb`, RR-03 `9c2d00a`, RR-04 `fbb09c3`, RR-05 `1350f2e`, RR-07 `1435ea9`. The source is ready for the requested independent re-review. The owner rollout checklist is prepared in `docs/CONFIGURATION.md`; private configuration remains owner-assisted.

## R6 catalogue completion — 15 November 2026

Installed code now registers domain descriptors, immutable activity aliases, workflow references and capability metadata. Voice capability risk comes from the installed code; domain definitions cannot install capabilities or change their risk. Conversation instructions resolve through the domain catalogue with the previous text preserved. Legacy `practice-plan` and `interview-brief` retain their stored identifiers/version1 and use registered executors; the worker no longer branches on their names. Legacy jobs remain separate from generic agent task start routes.

Fresh validation:35 affected backend tests passed after the executor refactor (`TestResults/r6-catalog.log`, 24s); seven catalogue tests passed after final legacy domain binding (`TestResults/r6-catalog-final.log`, 3s). Covers existing paid/unpaid workflow behavior with fake HTTP, persisted enum compatibility, prompt compatibility, two synthetic extension domains, defensive copying, unknown-capability rejection and duplicate activity-alias rejection. An initial compile found a nullable claimed-run access in the paid failure filter; the filter was corrected before these passing runs.

No dependencies, projects, migrations, private configuration, paid calls or deployment were added. Database registry/factory and provider-neutral routing remain subsequent slices.

## R7 provider-neutral text routing — 15 November 2026

Chat, coaching, agents and legacy interview briefs now call `ITextReasoningProvider` using provider-neutral messages, capabilities, data class and consent. Installed OpenAI/Anthropic codecs share reviewed RateCard selection and durable admission. Unsupported capability/destination combinations are refused before dispatch; no fallback or model downgrade is selected. Model/rates/key snapshots are fixed per attempt. The runtime stores content-free route/charge metadata and applies source-deletion/retention fences to cached text and attempt fingerprints.

Fresh results:46 affected tests passed (`TestResults/r7-affected-final.log`, 39s), then15 admission/adapter/legacy/architecture tests passed (`TestResults/r7-admission.log`, 14s). Final **full backend suite:289 passed, zero failures/skips**, including PostgreSQL restart/concurrency and privacy, 5.7minutes. Archived `tests/Jarvis.Tests/TestResults/r7-full.trx`; diagnostic log `TestResults/r7-full.log`.

Fixtures exercise the same normalized request through both codecs, cache reuse, denied data classes, unsupported grounding with no fallback, failed-attempt replay blocking, configuration changes during a response and deletion of a paid source while retaining its charge. An old legacy test expected a completed duplicate to throw; it now asserts matching cached content and exactly one provider call. Initial compilation exposed removed constructor dependencies; these were corrected before the passing runs. No live provider request, credential/configuration change, new dependency/project, migration or deployment was performed.

Final follow-up:two tests passed for recovery under a new worker claim and clearing an attempt fingerprint when its raw cache expires, even if the cache was read recently (`TestResults/r7-final-followup.log`, 4s). Six focused desktop/mobile browser checks passed (`TestResults/r7-browser.log`, 42.5s), covering consent, disabled paid readiness and transcription failure with no provider configured. These follow-ups do not represent another full-suite run.

Final Android follow-up: `:app:testReleaseUnitTest :app:lintRelease --offline --no-daemon --max-workers=1` completed successfully (3m23s), using the existing JDK/Gradle/cache with a 1GB build heap and in-process Kotlin compilation for this memory-constrained machine. Compilation, test execution and lint analysis ran on the final Android source. The JUnit XML totals are **18 tests, zero failures/errors**; the new case verifies that ordinary 401, offline and malformed-epoch failures preserve drafts, while confirmed erasure/valid generation changes request cleanup. No APK was assembled/installed and no hardware acceptance is inferred. The earlier pre-follow-up Android build also passed; it is superseded by this final result.

## R8 relational Domain Registry — 15 November 2026

Added six relational tables through migration `20261115141652_DomainRegistry`; historical migrations are unchanged. Immutable manifest versions, stable keys, approval hashes/revisions, idempotent activation/rollback/disable, canonical alias uniqueness, dependency/risk checks, user preferences, export/erasure and privacy epoch fencing are implemented. Registry-backed workflows resolve without deployment, pin versions in run policy, and deliver authored templates without AI calls. Disabled parents invalidate descendants. No provider credentials or private settings changed, no paid requests and no production migration/deployment.

Fresh validation:13 affected backend checks passed (`TestResults/r8-affected.log`, 36.8s), including PostgreSQL concurrency/restart/privacy, legacy migration upgrade and idle runtime projections. Final10 registry/catalogue checks passed after scheduling/parent-invalidation coverage (`TestResults/r8-final.log`, 25s test duration). Initial test execution exposed a test race with the hosted worker; these deterministic execution tests now disable hosted workflow processing and claim work explicitly. A final new test initially missed the existing goal revision argument; corrected before the final passing run. API build passed without warnings/errors. Matching local EF10.0.13 scaffolded metadata only; unused local EF11 tool did not change project dependencies. See `docs/DOMAINS.md` for schema, routes and operational limits.

## R9 manifest-only Domain Factory — 15 November 2026

Natural-language requests now use the neutral reasoning port, strict manifest validation and the existing approval lifecycle. Proposals preserve generation provenance and reuse persisted results, resolve existing names, default to isolated context, and never activate or grant privileges. Core voice can propose from a captured explicit user request only. Dashboard preview shows purpose, exact manifest/hash, capabilities, scope, cost mode and gaps before manual owner activation. Frontend source and Core bundle are synchronized.

Fresh validation:10 factory/registry/privacy checks passed (`TestResults/r9-affected.log`, 21s test duration). Two desktop/mobile approval-flow tests passed (`TestResults/r9-browser.log`, 29.9s), asserting exact hash/version confirmation, scoped owner header, literal rendering and responsive layout. API build and both JavaScript syntax checks passed. Tests use an injected manifest generator; no real provider call, paid activation, private configuration or deployment occurred. Existing R7 tests cover shared provider admission rather than duplicating fake admission logic here.

## TD-14 independent pause controls — 15 November 2026

Split paid admission from agent processing with backward-compatible nullable flags inheriting historical Paused values. Management normalizes the new flags; old persisted identifiers/contracts remain readable. Agent pause no longer revokes audio and audio availability is independent of both pause flags. Explicit stop/revocation/erasure still fences audio. Updated the former regression expectation that pausing clears audio, and added independent budget/claim/audio/legacy-state checks.

Fresh affected backend run:23 passed, zero failures/skips (`TestResults/td14-final.log`, 19s test duration). Two desktop/mobile browser control checks passed (`TestResults/td14-browser.log`). Initial compilation caught a default-interface call on a concrete test store; corrected. An initial run retained the old audio-revocation assertion; replaced with the required lease-preservation expectation before the passing run. No paid provider call, private configuration, new migration/dependency or deployment.

## TD-17 completion — 15 November 2026

Committed repair-based consistency for the existing separate session/runtime stores. Voice transcript messages now carry a durable marker and canonical speech duration; the runtime VoiceTurns projection is idempotently repaired after interrupted writes. Orphan paid audio leases are fenced and drained while preserving the source lease expiry. Repairs respect privacy epochs and retired sessions; idle PostgreSQL repair performs no runtime-row write.

Fresh validation before commit: API build passed (zero warnings/errors, `TestResults/td17-commit-build.log`). The focused voice/recovery run passed41 checks (`TestResults/td17-final.log`, 45s), including concurrent repair, restart, idempotence, erasure fencing, realtime transcript behavior, and drain preservation. One initial Postgres SQL expression used `{}` with EF SQL formatting and failed; changed to `jsonb_build_object()` and reran the full affected set successfully. No provider calls, new migration, deployment or private settings changes.

## RR2-02 paid realtime pause — 15 November 2026

The realtime lease monitor now checks the independent paid-AI pause every second. A paid realtime session ends with `paid_ai_paused`, clears pending provider work, and settles the usage reservation. The pause does not revoke the local audio lease; the normal stream cleanup releases the lease after the paid session stops.

Fresh validation: API/test project build and two focused tests passed, zero failures/skips (`tests/Jarvis.Tests/TestResults/intelligence.trx`): the new pause/settlement/local-audio test and existing invalid-ticket rejection test. No live provider call or private configuration change.

## RR2-03 shared work eligibility — 15 November 2026

Agent tick, task claim and push dispatch now use shared eligibility rules before and inside their write transactions. Rules include expiration/recovery work and honor workflow availability, consent, pause state, quiet hours, delivery lease/retry state and schedule windows. The transactional recheck remains in place for races.

Fresh validation: API/test build succeeded and13 focused tests passed, zero failures/skips (`tests/Jarvis.Tests/TestResults/intelligence.trx`), covering idle write avoidance, goal-deadline transition eligibility, stale task lease recovery, push dispatch, and planning behavior. No new dependencies or migrations.

## RR2-05 voice domain proposal phrases — 15 November 2026

Captured voice proposals now accept explicit `add`, `create`, `build`, `make`, `extend`, `configure`, `set up` and `start` requests that name a domain, module, programme/program, course or subject. General requests still cannot invoke proposal generation.

Fresh validation: API/test build succeeded and13 DomainFactory tests passed, zero failures/skips (`tests/Jarvis.Tests/TestResults/intelligence.trx`). The three exact examples in `CLAUDE.md` and additional supported/rejected phrase cases are covered. No provider request beyond injected fixtures.

## RR2-04 domain approval risk — 15 November 2026

Owner preview now displays the inferred risk class and a confirmation-bound approval risk choice. General drafts may be raised to a sensitive class; inferred sensitive classes cannot be lowered or exchanged, and a previously approved sensitive class cannot be reduced on later activation. Risk-specific capability checks run at activation and active-domain validation. Approval risk is stored in the relational approval record; the additive migration backfills existing approvals from their version manifests.

Fresh validation: six DomainRegistry/PostgresDomainRegistry tests passed, zero failures/skips, including persisted approval risk across a PostgreSQL restart (`tests/Jarvis.Tests/TestResults/intelligence.trx`). Desktop and mobile domain-preview browser checks passed (2 total), confirming the selector and submitted risk (`TestResults/browser-domains.log`). EF reported no pending model changes after migration scaffolding. No production migration/deployment or private settings change.

## RR2-01 historical RR-03 commit build boundary — 15 November 2026

Built commit `9c2d00a` independently in a temporary detached worktree with the repository-pinned .NET 11 SDK. `dotnet build Jarvis.sln --verbosity minimal` failed with six C# syntax errors at `src/Jarvis.Api/Notifications/PushNotifications.cs:109`, caused by an invocation-origin statement left after the file's closing brace. The later RR-07 commit `1435ea9` removes that stray statement; the current tree builds. This is recorded as historical evidence only; no commit was amended or rewritten.

## R12 local wake and Core arbitration — 15 November 2026

Core exposes `POST /api/voice/wake-candidates` to authenticated audio devices. It requires explicit cloud processing consent, prepares the canonical companion session, and obtains the existing global audio lease and single-use voice ticket. The lease is the cross-device response arbitration fence; this endpoint does not itself invoke an AI provider.

Android adds an opt-in foreground microphone service using only Android's on-device speech recognizer for the wake phrase. A detected phrase submits a candidate to Core; only a winning candidate proceeds into the existing live voice path. The UI explains that live audio after wake is sent to the configured provider. Local detection requires Android 12+ and an installed/available English local recognizer; there is no cloud speech fallback. The owner must enable it in the app while visible and can stop it from the persistent notification.

Fresh validation: the Core API/test build and the focused HTTP arbitration test passed (one test; first eligible device receives a ticket and a competing device receives conflict). Android `:app:testReleaseUnitTest :app:lintRelease --no-daemon --max-workers=1` passed; JUnit XML reports 23 tests with zero failures/errors/skips, and release lint passed. No APK was installed. No physical-device acceptance was performed, so screen-off reliability, Bluetooth earbud routing and Android background survival remain unverified; always-on support is not claimed.

The updated release APK was then assembled successfully with `:app:assembleRelease --no-daemon --max-workers=1` (48 actionable tasks; 29 executed, 19 up-to-date). Artifact: `JARVIS.MOBILE/app/build/outputs/apk/release/app-release.apk`, 20,867,507 bytes, SHA-256 `E02C9866037F5CF822C9EC7CCB9054CD1D326C3A1B9D0E144C8DBE98593F5EE1`. The APK was not installed.

## RR2-06 companion preparation query scope — 15 November 2026

Added a device-scoped preparation projection. PostgreSQL filters to the requesting device's sessions, matching handoff targets, paused unclaimed `JARVIS voice` sessions, and the deterministic session/checkpoint ID before parsing message counts. Checkpoint IDs are queried only for those candidate sessions. The compatibility default preserves existing non-Postgres readers; no migration or route change was needed.

Fresh validation after the change: the focused PostgreSQL projection test passed (one test) and the Core wake-candidate HTTP arbitration test passed (one test). The former asserts an unrelated active session owned by another device is omitted; the latter verifies one audio-ticket winner and a conflict for a competing device. Both runs built API and test projects successfully.

## TD-19 atomic AI-coaching assessment admission — 15 November 2026

Coaching AI assessment now acquires a five-minute, token-fenced claim in the shared runtime state before dispatch. It atomically rechecks the current answer/source, assessment-history cap, duplicate request ID and any in-flight claim for the same attempt. A successful response consumes its claim in the same update that saves the assessment; failures release it, and stale claims expire. Deleting/retaining the source removes its claims. No database migration or project/dependency change was needed.

Fresh validation: all six `AiCoachTests` passed (zero failures/skips). The new concurrent test submits six distinct assessment requests against one answer while the fake provider is blocked; exactly one provider dispatch and saved assessment succeed, and five requests receive conflict. Existing consent, idempotency, fabricated-evidence, source-change and deletion-race checks also pass. Provider calls use only the local fake HTTP adapter.

Follow-up: expired claims are included in `RuntimeMaintenance`'s read-only expiry precheck and periodic pruning. The combined focused run passed seven tests (six AI-coach cases plus the maintenance test), zero failures/skips; it confirms an expired claim is removed while financial reservations remain untouched.

## TD-27 expired agent notification pruning — 15 November 2026

The periodic runtime maintenance sweep now removes expired agent outbox notifications and their dependent push-delivery records. It leaves active, unexpired notifications and canonical financial reservations intact. This bounds transient outbox growth independently of the operator-controlled privacy-retention setting; no private configuration was edited.

Fresh validation: the focused cache-maintenance test passed (one test, zero failures/skips), asserting expired notification/delivery cleanup, preservation of an active notice, and preservation of the usage reservation.

## TD-12 application ports below the API — 15 November 2026

Moved `IAgentExecutor` into `Jarvis.Orchestrator.Workflows` and `IRealtimeConnectionFactory` into `Jarvis.Orchestrator.Intelligence`. Implementations remain in the API integration/voice adapters, and existing service behavior and runtime contracts are unchanged. API DI now binds implementations to the Orchestrator-owned ports; no project, package, route or persistence changes were made.

Fresh validation: the API/test build succeeded and 18 focused tests passed (zero failures/skips) across realtime protocols, agent/domain execution and CQRS architecture.

## TD-13 agent write endpoints through CQRS — 15 November 2026

Agent goal, trigger, control, schedule, run-start/cancel and notification-read writes now dispatch through registered Orchestrator command handlers. Endpoint route shapes and result types are unchanged; agent business rules remain in the existing `AgentRuntime`, and notification mutation now resides in the application handler. Agent GET projections and internal calendar-worker calls remain as-is.

Fresh validation: the API/test build succeeded and eight focused tests passed (zero failures/skips), including complete handler registration, endpoint delegation to an injected goal command handler, agent runtime eligibility and idle-work behavior.

## TD-13 import write endpoints through CQRS — 15 November 2026

Document import, source-claim attachment and import deletion routes now dispatch through registered Orchestrator command handlers. Existing routes and result types are unchanged; validation, idempotency, source invalidation and deletion behavior remain in `ImportService`. Read-only import projections and paid assessment dispatch remain unchanged.

Fresh validation: the API/test build succeeded and eight focused tests passed (zero failures/skips), covering CQRS registration, endpoint dispatch to an injected import command handler, CV claim validation/idempotency, deletion cleanup and interview transcript validation.

## TD-13 AI assessment dispatch through CQRS — 15 November 2026

Coaching, technical-training and imported-document AI assessment routes now dispatch through Orchestrator command handlers. `IAiAssessmentService` is the application-owned port implemented by the existing budgeted `AiCoach` API adapter. Rate-limit policies, endpoint routes and result contracts are unchanged; provider reservation, consent and evidence validation remain in the adapter.

Fresh validation: the API/test build succeeded and 13 focused tests passed (zero failures/skips), covering CQRS handler registration and route delegation plus AI-coach consent, concurrency admission, provider-cache behavior, evidence validation and deletion/source-change races. Two initial build attempts exposed missing namespace imports in the moved port references; imports were corrected before the passing run. Tests use only fake provider HTTP.
