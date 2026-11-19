# JARVIS 0.3 — draft release notes

18 November 2026. Local candidate: feature/travel-push-release in W3 JARVIS-PLATFORM. Exact final tested tip is recorded in CODEX_6H_RUN_LOG_2026-11-18.md and the travel push report. This is a source-review packet, not deployment approval. The v2 run plan prohibits pushing, release-branch merges, image creation, deployment and installation during this run.

## Changes

Locked voice turns retain restricted provenance and cannot feed personal context, learning or private tool actions. Windows provides an offline wake companion and seven bounded governed capabilities, with local allow-lists, signed commands, receipts and replay protection. Agent tools use the existing code-owned registry, scoped approval/grants and budget accounting. Governed proposals, captured unlocked reversible voice approval, cancellation and idempotent manual retries are available.

The existing domain registry covers legacy activity/learning aliases, owner-reviewed domain drafts, private scoped context and source provenance. Published claims require evidence. An offline evaluation set guards structure, grounding, tool choice and refusal behavior; paid evaluation remains disabled and unrun. Speech delivery metrics and four registry-backed audio coaching scenarios include session trends and debrief evidence.

Hot audio/control state, voice tickets and turns move to D2 relational tables with backfill and dirty tracking. The final measured synthetic control-update p95 was 29.702 ms against the unchanged 50 ms gate. Synthetic Core wake-to-ticket p95 was 2894.936 ms over forty attempts; this is not microphone/provider/network latency. Cold artifact/financial paths retain document costs. Browser enrollment and five-minute scoped owner elevation reuse existing device credentials; Authentication:RequireEnrolledBrowser remains false by default.

Saved London routes have owner browser editing, validated public IDs, London windows, public locked-safe status/arrivals voice and restart-safe debounced monitoring. Locked travel accepts only an explicitly named public line, refuses saved stops/routes and adds no saved-route context to locked windows. Travel extends the existing notification outbox and encrypted Web Push with public line summaries only. Generic categories keep the private-inbox body. Travel exceptions to quiet hours are bounded to opted-in windows; agent-pause behavior is configurable. Expired travel pushes are discarded in the service worker. Owner-only synthetic simulation is off by default and always refused in Production.

## Source-review ledger

Reviewer reports CLAUDE_RUN_REVIEW_ITEMS_1-5_2026-11-18.md, CLAUDE_RUN_REVIEW_ITEMS_6-10_2026-11-18.md and CLAUDE_RUN_REVIEW_ITEMS_11-14_2026-11-18.md approve the source listed below. The reviewer did not independently rerun the tests.

| Item | Commit(s) | Review state |
|---|---|---|
| 1 restricted voice boundary | 1647a87 | Approved; L15A-01/02/03 closed |
| 2 Windows companion/greeting | b37f639, 843b31d, b151f71 | Source approved; hardware/install pending |
| 3 registry/tool policy | c20b04e | Approved; R-03 follow-up closed below |
| 4 governed actions/voice/retry | f71c00a | Approved |
| 5 Windows governed executor | 4877a98 | Source approved; R-01 fixed below; capability-list/install acceptance pending |
| 6 domain persistence audit/aliases | 00798cf | Approved |
| 7 domain factory/context | f3f09b9 | Approved with low R2-01 follow-up |
| R-01–R-04 fixes | 5b8267b | Approved; all four closed |
| 8 evidence/provenance | 8cf4e68 | Approved; live quality evaluation still pending |
| 9 offline intelligence evaluation | 2fea11d | Approved; optional paid run requires separate authorization |
| 10 speech delivery | 59ae728 | Approved with low R2-02 follow-up |
| 11 coaching scenarios | bba23cb | Approved |
| R2-01/R2-02/R2-06 fixes | b4238e5 | Approved; all three closed |
| 12 hot persistence | 27c1a51 | Source approved; R3-04 restore follow-up in Item 15 |
| 13 browser enrollment/elevation | 7e7ab61 | Approved; R3-02/03 follow-up in Item 15 |
| 14 routes/voice/monitor | 05b9f85 | Approved with R3-01 follow-up in Item 15 |
| 15 travel push/simulation/these notes | This candidate change; final hash in run log | Source review pending |

4877a98 integrates W1 on the feature chain. Release integration must retain a clean, reviewable sequence and all dependencies. No development/master integration or remote CI execution is claimed. R2 information items remain: prefer exact approvals for third-party-import planners, and collect paid refusal/cost/grounding evidence before any model tuning. No model, rate or price change was made.

## Migration order and rollback

EF applies the following existing chain in order. No historical migration was removed or rewritten.

1. 20261030034425_InitialSessions
2. 20261030035632_RuntimeFeatures
3. 20261113031234_WorkflowRuns
4. 20261113221555_PrivacyControl
5. 20261115141652_DomainRegistry
6. 20261115181441_DomainApprovalRisk
7. 20261115233000_PendingVoiceProjection
8. 20261116061743_DeviceGrants
9. 20261116101533_DeviceGrantAudit
10. 20261116104500_ReconcileBeforeAtomicCoreWrites
11. 20261116114656_DeviceCommands
12. 20261116121125_DeviceCommandDelivery
13. 20261116123244_DeviceReceipts
14. 20261116154900_VoiceHomeGrants
15. 20261117120049_SessionVoiceSummary
16. 20261117225515_LockedVoiceHomeGrants
17. **20261118153806_RuntimeHotRows** — new in this run; audio lease/drain, tickets and typed turns backfilled, then removed from cold JSON.
18. **20261118184236_RuntimeHotControlState** — new; controls, devices, credentials and wake attempts backfilled to the hot singleton.
19. **20261118194620_TravelRoutesAndMonitor** — new; three dedicated travel tables and singleton seed. Item 15 adds JSON metadata only, without another migration.

Before an authorized rollout, stop all Core writers, take and verify a database backup, retain matching binaries/private settings securely, and inspect migration history. Apply with the matching reviewed binary; do not run old and new writers together. Migration rehearsal used only disposable pinned PostgreSQL 17.

Travel Down drops all travel tables: export/backup first or route configuration and monitor history are lost. Its upgrade/down/reapply rehearsal preserved existing runtime JSON. Hot-state Down reconstructs the current hot data into runtime JSON before dropping the relational hot tables; its earlier rehearsal verified source hashes and audio authority through down/up. For complete rollback of this run's schema, reverse travel first, then HotControlState, then HotRows to LockedVoiceHomeGrants. Stop writers and pair the resulting schema with the corresponding old binary. A matching backup is the recovery path if any downgrade or verification fails. Public notification delivery already accepted by a push provider cannot be recalled by a database rollback.

## Owner and operator steps after source review

1. Decide the release/integration sequence after reviewing Item 15 and its R3 follow-ups. Live release remains unapproved by this packet. Remote CI must run after an explicitly authorized push.
2. Approve the Windows seven-capability list before installation. Set matching Core grants and Windows app/URL/folder allow-lists. Sign the reviewed unsigned MSIX through the owner's signing process, install only when separately authorized, then check microphone/speaker, offline wake, media/volume/mute, lock-to-silence behavior and Windows/phone arbitration. Local fake/native-interface tests do not replace these checks.
3. Enroll a browser under Connections, save its scoped credential securely, test reconnect and temporary owner unlock. Only then consider Authentication:RequireEnrolledBrowser=true. It is false by default. Invalid values fail startup; an invalid reload returns 503 until the operator corrects the flag. Erasure recovery requires temporarily disabling that flag, rotating the configured Core key, reopening with the separate owner key and enrolling again before re-enabling it. No private settings were edited during this run.
4. Obtain the free TfL app key and enter TfL:AppKey in the existing private root appsettings.json. Configure the existing Push:Enabled, Push:Subject, Push:PublicKey and Push:PrivateKey fields with the owner's VAPID keys. Leave Travel:AllowSimulation=false in Production. Travel:NotifyWhenAgentsPaused defaults true; choose false if travel must stop with agents. Do not overwrite existing secrets or copy private settings into source control.
5. At the trusted Tailscale HTTPS address jarvis.your-tailnet.ts.net, use Chrome on the actual S24/S22, connect/enroll, configure My routes in Daily briefing and enable browser notifications under Connections. Permit notifications in Chrome/Android and check background/lock-screen behavior. After an update, close and reopen JARVIS tabs so the new service worker activates, then verify the travel notification title and public summary. No Tailscale settings, certificate trust, phone installation or native feature screens changed here.
6. Rehearse simulation only in a separately authorized non-Production environment with Travel:AllowSimulation=true. Create an active saved route. POST /api/travel/simulate twice with distinct request IDs, its public lineId and state=severe; an exact request replay cannot count as the second observation. Every fixture notification says Simulation. Record request/phone-display timestamps only after the owner observes it. Wait past the fifteen-minute per-line cooldown, then send two distinct state=good requests and check recovery. Never enable this endpoint in Production; Production always refuses it. Disable simulation after rehearsal. No automatic or live paid/provider smoke test is part of these steps.
7. Run the optional paid evaluation only with separate owner authorization, step-up, explicit consent and the existing budget limits. Offline checker success does not establish live model quality or calibrated acoustic scores.

## Verification and limits

Final Core/PostgreSQL verification passed 733 tests, zero failures and one existing opt-in benchmark skip (734 total, about 13m52s). The final complete desktop/mobile browser run passed all 76, zero failures/skips/flaky results. Release build had zero warnings/errors. Fifteen Kotlin contracts, frontend parity, EF model and whitespace checks passed. Detailed evidence is recorded in CODEX_TRAVEL_PUSH_2026-11-18.md and CODEX_6H_RUN_LOG_2026-11-18.md. Item 14 passed 104 focused checks, eleven final voice/contracts checks, two PostgreSQL restart/migration checks and four desktop/mobile editor checks. Item 15's focused push/architecture group passed 23 checks before the final simulation-label edit; the final full suite covers that edit.

The reviewed unsigned Windows actions package remains Jarvis.Windows.Actions_0.1.0.0_x64_unsigned.msix, 60,735,612 bytes, SHA-256 6435BCE3BDCF5E4E8320CC199EAFDA3C56AC8968FE5E974D0ED8B33EE20B4A4B. It was not signed, installed or launched.

Physical simulated-disruption-to-phone latency: **unmeasured**. Physical recovery notification: **unverified**. Live TfL availability, microphone recognition, device audio routing, lock timing, wake arbitration and OS background delivery remain owner acceptance work. Item 15 and its review fixes need source recheck; physical acceptance remains required before release. No image was built and nothing was pushed, merged to a release branch, deployed or installed.


Round-3 follow-up in this candidate: R3-01 removes saved travel details from locked voice; R3-02 refuses browser enrollment over non-browser identities; R3-03 tests that owner-browser heartbeat/audio renewal remain bound to its device even with elevation; R3-04 seeds non-empty voice/travel fixtures and checks all new relational tables after restore; R3-06 validates the security migration flag at startup and documents fail-closed reloads. R3-05 remains the documented future performance boundary for async cold preload and per-session turn queries. These follow-ups await reviewer recheck.
