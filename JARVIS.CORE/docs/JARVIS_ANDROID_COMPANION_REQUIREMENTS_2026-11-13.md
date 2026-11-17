# JARVIS Android Companion Implementation Requirements

Version 1.0 | 13 November 2026 | Implementation handoff for Codex

## 1 Purpose and authority

Build `Jarvis.Android`, a voice-first Android companion for interview preparation, technical retrieval practice and everyday communication coaching. Use React Native and TypeScript for the application, with a narrowly scoped native Android audio module. Keep ASP.NET Core JARVIS Core responsible for intelligence, permissions, durable state and cross-device coordination.

This specification extends `JARVIS_REQUIREMENTS_vNext_2026-11-12.md`, dated 12 November 2026, which was read in full. It is a mobile implementation contract, not a replacement for the wider Personal AI Operating System. The baseline's product intent and privacy boundaries remain authoritative. The lighter stack and detailed defaults below are new implementation decisions. Endpoint names and schemas are proposed contracts, not assertions about an existing repository.

Codex must inspect the existing solution before changing it, map existing equivalents to this specification, and reuse compatible modules. Do not create parallel authentication, coaching or memory systems. Record any mapping or necessary deviation in an architecture decision record. No repository implementation has been audited as part of this document.

MUST indicates a release requirement; SHOULD permits a documented trade-off; MAY is optional. P0 is the usable mobile release. P1 completes seamless handoff and import/proactive workflows. P2 is deferred research. A P0 simplification must not be presented as delivery of the full baseline capability.

## 2 Product outcome and scope

The primary experience is: sign in, connect earbuds, choose Continue training, hear a question, answer aloud, receive concise feedback, save progress and later resume on the laptop. A user must be able to complete this while the phone is locked after explicitly starting the session on screen.

Success means the user can explain technical concepts and real CV claims accurately, simply and with clear personal ownership, trade-offs, failure behaviour and outcomes, using a calm and context-appropriate pace. Coaching must preserve the user's Nigerian accent and identity. A British-influenced assistant voice is a configurable presentation preference, not a target accent for the user.

### 2.1 Release boundaries

| Capability | P0 | P1 or later |
|---|---|---|
| Voice | Streamed turn-based voice, automatic turn detection, explicit interrupt, screen-off session | Validated full-duplex barge-in and speech-to-speech adapter in P1 |
| Earbuds | Android audio routing, play/pause, stop, repeat/skip in app and supported media controls | Additional headset mappings only where exposed by OS |
| Coaching | Technical drills, Story Bank, CV defence, everyday speech drills, scores and due reviews | Advanced adaptive plans and richer weekly summaries |
| Continuity | Server checkpoints and explicit stop/resume between devices, one audio owner | Live request/checkpoint/ready/transfer/confirm handoff |
| Offline | Downloaded drill pack, text prompts, cached/device TTS where available, local answers with consent | Optional on-device speech recognition after separate evaluation |
| Interview review | Mock summaries and spoken/typed post-interview debrief | Consented recording/transcript import and speaker correction |
| Notifications | Opt-in local practice reminders | Server-driven schedule changes and push reminders |
| Programme | Editable 12-week plan and daily practice tracking | Calendar-informed coaching after integration approval |

P0 includes all named interview and technical modes as configurations of shared session logic. It does not require a separate bespoke screen or model for each mode. P1 is required before claiming complete coverage of the baseline's real-time voice and seamless continuity goals.

### 2.2 Explicit exclusions

Do not implement always-listening background wake words, OS assistant replacement, covert capture, phone-call interception, live answer feeding during real assessments, automatic recording of other apps, screen overlays over interview apps, or automatic acceptance of recording consent. Even where an assessment permits assistance, live assessment assistance is outside this companion release.

Also excluded: mobile code execution/IDE, full diagram editor, model training, local large language models, iOS release, smartwatch app, Meta Quest, far-field room hardware, smart-home control, payments, social-message sending, autonomous external tools, camera analysis, location tracking and contact/calendar ingestion. Existing JARVIS Core capabilities may remain accessible through other clients; this app does not expose them by default.

## 3 Source traceability

| Baseline section | Mobile implementation |
|---|---|
| 1 Product vision | Sections 2, 6 and 7; communication beyond interviews |
| 2 Core architecture | Sections 4, 11 and 12; modular monolith and PostgreSQL retained |
| 3 Voice-first operation | Sections 5 and 8; P0 turn-based fallback, P1 full duplex |
| 4 Communication coach | Section 7; audio-derived metrics and everyday drills |
| 5 Interview storytelling | Section 6; Story Bank, modes, CV defence, review |
| 6 Technical trainer | Section 6; full topic taxonomy and mobile verbal practice |
| 7 Adaptive learning | Sections 6 and 12; shared scores and review scheduling |
| 8 Twelve-week programme | Section 6.5 |
| 9 Mobile practice | Sections 5, 9 and 13 |
| 10 Proactive coaching | Sections 6.5 and 13; opt-in reminders |
| 11 Interview listening | Sections 6.4 and 10; post-interview import only |
| 12 Session continuity | Section 8; P0 resume and P1 live handoff |
| 13 Home integration | Explicitly outside mobile scope |
| 14 Privacy | Section 10 |
| 15 Priorities | Sections 2 and 16; mobile P0 is elevated, live handoff stays P1 |
| 16 Success criterion | Sections 2, 7 and 17 |

## 4 Architecture and lightweight stack

### 4.1 Decision

Use React Native, TypeScript and Expo development builds with prebuild/config plugins. Do not target Expo Go for production audio behaviour. This reduces mobile UI implementation effort and supports shared typed contracts; it does not guarantee a smaller binary than a native Kotlin application. Keep Kotlin limited to Android platform features that prove necessary. Use a maintained compatible library where it meets the acceptance tests; own a small local Expo module where it does not.

A PWA/Capacitor-only implementation is rejected for this release because reliable locked-screen microphone operation, media controls and lifecycle recovery are central requirements. Flutter adds Dart without a demonstrated advantage for this project. A fully native Kotlin application remains a fallback only if the hardware spike proves the selected approach cannot meet mandatory audio behaviour; record evidence before changing the decision.

### 4.2 Components

```text
React Native screens and session state
  | REST JSON + SignalR control events
  | native audio service -> authenticated binary WebSocket
  v
Jarvis.Api -> Orchestrator -> Voice / InterviewCoach
                          -> CommunicationCoach / TechnicalTrainer
                          -> Memory / Coaching
  | PostgreSQL: authoritative sessions, progress and jobs
  | encrypted object storage: consented recordings only
  | Worker: scoring, imports, summaries and deletion
  v
Server-side speech and model provider adapters

Windows / room clients -> same ownership and checkpoint contracts
```

No mobile database connection, permanent provider key, direct privileged tool execution, or independent mobile learning history. No mandatory Redis, Kafka, Kubernetes, vector database or microservices. Use a PostgreSQL job/outbox table and the existing `Jarvis.Worker` for durable asynchronous processing. Add distributed infrastructure only after a measured requirement.

### 4.3 Technology choices

| Area | Choice and boundary |
|---|---|
| Mobile | React Native + TypeScript strict mode; Expo development build |
| Navigation | Expo Router; small native component set with accessible styling |
| Server data | TanStack Query; persisted cache limited to approved data |
| Session state | Explicit reducer/state machine; no separate global state library initially |
| Local data | Expo SQLite with SQLCipher enabled in native build; migrations and per-account database |
| Secrets | Expo SecureStore/Android Keystore-backed storage; no AsyncStorage tokens |
| Native audio | Local `jarvis-audio` module, Android foreground service, AudioRecord/AudioTrack or validated equivalent, Media3 MediaSession integration |
| Non-streaming audio | `expo-audio` only where compatible; it must not compete with native service for capture/focus |
| API | Existing supported ASP.NET Core target; REST/OpenAPI + official SignalR client |
| Persistence | Existing EF Core/Npgsql/PostgreSQL conventions; encrypted audio object storage |
| Authentication | Existing OIDC/OAuth authorization code with PKCE in system browser |
| Tests | Existing .NET test framework, React Native Testing Library, device E2E runner such as Maestro, native instrumentation |
| Diagnostics | Existing structured logs/OpenTelemetry; privacy-filtered mobile diagnostics |
| Build | Local Android build supported; hosted Expo build optional, not a runtime dependency |

At milestone M0 pin a compatible stable Expo/RN/Node/JDK/Gradle/Android SDK set and commit the lockfile and toolchain instructions. Do not independently choose the newest version of every library. Use the repository's supported .NET version; do not upgrade the backend merely to add Android. Set minimum Android to API 29 or the chosen supported stack's higher minimum, record the result, and verify the user's Samsung model before promising support. Target SDK must meet the distribution channel's rules at release time.

### 4.4 Deployment

The Core endpoint must be reachable from the phone through trusted HTTPS, either hosted or through an explicitly configured private VPN. A laptop-only localhost service will not work away from home; document this prominently in setup. Never expose unauthenticated development ports or disable TLS validation in a release build. Provide separate development and production configuration, health probes, migrations and backup/restore instructions. The default is one API deployment plus one worker sharing PostgreSQL, with the option to run worker jobs in the same deployment for a single-user setup.

## 5 Voice and earbud requirements

### 5.1 State machine

`Idle -> Preparing -> Prompting -> Listening -> Processing -> Feedback -> Listening`; any active state may transition to `Paused`, `Reconnecting`, `Ending` or `Error`. Ending returns to Idle after releasing microphone, audio focus, stream and ownership. UI must expose text labels and icons; colour alone is insufficient.

- VOI-01: Start only after a user action in a visible activity, microphone permission and a server ownership grant. Start the required foreground service while the app is eligible, before going to the background.
- VOI-02: Prompting plays assistant output; Listening captures user audio. P0 uses half-duplex operation, with capture paused during assistant playback. The app displays and optionally tones the transition to Listening.
- VOI-03: Automatic end-of-turn default is 1.5 seconds of silence after speech starts. Make it adjustable from 0.8 to 3 seconds, with a slow-speech preset. A 1–2 second thinking pause before the first spoken word must not submit an empty answer. After 15 seconds with no speech, offer retry; do not score silence as an incorrect answer.
- VOI-04: Default maximum answer is 180 seconds; deep-dive mode allows 300 seconds. Warn softly 15 seconds before the limit. Finish answer is always available. Limits are server-enforced too.
- VOI-05: P0 interruption is an explicit UI/media command that cancels playback, clears queued audio and moves to Listening only if the session is active. Voice barge-in while the assistant speaks is P1, gated on echo cancellation and actual headset testing.
- VOI-06: Listen for commands only inside an explicitly active Listening state. Support pause, stop, repeat question, skip question, slower, explain that and continue. No ambient wake word is needed. Distinguish a quoted command inside an answer from an explicit command intent; uncertain skip/stop intent requires a short clarification.
- VOI-07: Show current route, connection quality, muted/listening state, elapsed answer duration and unsynced status. Persistent notification uses generic text with Pause/Resume and Stop; no CV content on the lock screen.
- VOI-08: On calls, audio-focus loss, microphone revocation or OS interruption, immediately pause capture and playback, mark partial answer and save a checkpoint. Do not capture call audio. Resume only after an explicit user action and permission/focus revalidation.
- VOI-09: On earbud disconnection pause immediately. Do not unexpectedly read sensitive content on the loudspeaker or switch to the phone microphone without confirmation. Offer reconnect or use phone audio.
- VOI-10: Force-stop/process death never auto-restarts listening. On next launch offer recovery of the last durable checkpoint. An interrupted partial answer may be recovered only if it was explicitly stored; otherwise explain that it must be repeated.

### 5.2 Media controls

| Input | Behaviour |
|---|---|
| Play/pause | Pause active conversation; resume a paused authorised session after eligibility checks |
| Stop | Stop capture/playback immediately, checkpoint, release ownership |
| Previous | Repeat current question; preserve submitted attempt, discard only unsubmitted capture after confirmation |
| Next | Skip current question; record skipped, not failed; advance once |
| Phone volume | OS volume behaviour; no interception |
| Unsupported gesture | No custom assumption; show on-screen alternatives |

Android delivers semantic media actions, not universally consistent earbud gesture counts. Do not promise double-tap/triple-tap mappings or cold-start recording through earbuds. Debounce duplicate actions within 300 ms and make commands idempotent. Resume from locked state is allowed only for a still-valid user-started foreground session; otherwise request opening the app. Validate at least the user's headset plus one other Bluetooth headset and wired/USB audio where available.

### 5.3 Native boundary

The native service owns audio capture/playback, audio focus, route changes, media commands, foreground notification, monotonic audio timing and network/audio buffering needed while JavaScript is suspended. JavaScript owns screens and high-level orchestration; it must not be the sole keeper of background heartbeats or microphone lifecycle.

Expose typed methods `prepare(config)`, `start(streamGrant)`, `pause(reason)`, `resume(grant)`, `cancelOutput(responseId)`, `finishTurn()`, `setRoute(routeId)` and `stop()`. Emit sequenced events for state, route, focus, levels, turn boundary, command, lease expiry and errors. Do not send raw PCM samples across the JS bridge at audio frame frequency. A process restart queries a native state snapshot instead of assuming the previous UI state remains true.

## 6 Training requirements

### 6.1 Interview and technical sessions

TRN-01: Support Recruiter Screen, Hiring Manager, Engineering Manager, Technical CV Deep Dive, Senior Engineer, Staff/Principal Challenge, Behavioural, System Design, Code Review, Production Incident, Payments/Fintech and Mock Phone Screen modes. User chooses duration, difficulty, context and feedback style. Mock mode defers coaching until the end; coached practice gives one focused improvement after each answer.

TRN-02: Technical domains must include C#/.NET (async, concurrency, DI, collections, memory, ASP.NET Core, EF Core, Dapper, LINQ, exceptions, tests and design); distributed systems (messaging, RabbitMQ, Kafka concepts, delivery semantics, idempotency, outbox/inbox, retries, jitter, circuit breaking, backpressure, consistency, sagas, caching and tracing); databases (transactions, isolation, locks, concurrency, indexes, query plans, pools, modelling and reconciliation); APIs/security (REST, gRPC, versioning, OAuth/JWT, authorization, OWASP, rate limits, validation, secrets and audit); production (CI/CD, containers, Kubernetes concepts, deployment/rollback, migrations, flags, observability, incident response, SLOs and capacity); payments (duplicates, pending/unknown states, timeouts, reconciliation, settlement, maker-checker, fraud, FX and integrations); system design; and algorithms/data structures/complexity.

TRN-03: Share this taxonomy and learning history across mobile and laptop. Offer Rapid Drill with 20–40 second answers, Deep Dive, Explain Simply, Architecture Challenge, Failure Injection, Production Incident, Code Review Reasoning, Random Mixed Interview and Spaced Repetition. Mobile coding is verbal reasoning or read-only snippets; actual coding transfers to the laptop.

TRN-04: Coach project answers using Frame → Problem → Personal Ownership → Decision/Action → Outcome → STOP. Coach architecture using Requirement → Constraints → Architecture → Why → Trade-off → Failure Mode → Recovery → Scale. Do not require every answer to recite every step if irrelevant.

TRN-05: Detect generic answers and request the exact service, personal contribution, difficult decision, accepted trade-off and outcome. CV Attack Mode must refer to an actual saved claim and evidence, never invent a transaction count, availability figure, employer or personal achievement. Unknown claims remain unverified. Explanations should distinguish suggested wording from verified experience.

### 6.2 Story Bank

TRN-06: Support creation, edit, read, archive, practice and delete. Store company/project, exact service, context, problem, constraints, personal ownership, decisions, trade-offs, implementation, collaboration, failure modes, outcome, metrics, what would change today, likely follow-ups and linked CV claims. Allow missing fields and flag readiness gaps instead of fabricating content.

Seed story titles only, with no invented details: Alternative Bank FX Transaction Module, Digital Banking Platform, fraud/security incident, device-ID incident; Zedcrest transaction reconciliation; Access Bank maker-checker; deployment incident; technical disagreement; mentoring; technical debt trade-off; performance/scaling improvement; third-party integration failure. The baseline target is approximately 12–15 real stories, not a mandatory maximum.

TRN-07: Support 30-, 60- and 120-second retellings and follow-up defence. Draft changes from coaching require user review before modifying the canonical story. Story readiness shows missing evidence and practice history, not an unsupported certification of competence.

### 6.3 Scoring and scheduling

TRN-08: Score applicable dimensions from 0–4: technical accuracy, specificity, ownership, architecture reasoning, trade-offs, communication structure and recall stability. Anchors: 0 no usable evidence/incorrect; 1 major gaps; 2 partial; 3 clear and defensible; 4 clear with justified depth. Missing evidence is null, not zero. Audio measurements are separate from language-model ratings. Confidence is a user self-rating and an explicitly uncertain coaching observation, never a psychological diagnosis or accent-based score.

TRN-09: Each score includes dimension, rationale, answer evidence, assessor/model version, rubric version and confidence. Show one strength, one priority and one next drill. A user may correct a transcript and request rescoring; preserve the previous assessment version, make the replacement canonical and reverse the old learning contribution transactionally. Repeated jobs must not double-count progress.

TRN-10: Use a simple deterministic initial review rule. A canonical applicable content score below 2 schedules tomorrow; 2 to below 3 in three days; 3 or above in seven days, extending to fourteen after two successive strong attempts. Skips, absent audio and failed processing do not lower mastery. Due dates use the user's configured timezone; server timestamps remain UTC. Record the rule version and allow user overrides.

### 6.4 Debrief and imports

TRN-11: “Interview just finished” starts a debrief, asks for questions while fresh, captures remembered answers, uncertainty and follow-ups, and produces an editable review. Mark reconstructed answers as recollections, not verbatim transcripts. Extract strengths, generic answers, missing ownership/depth, hesitation, communication issues and future drills.

TRN-12, P1: Import only via Android document picker or intentional share action, with a consent statement confirming authority to process the content and acknowledgment of cloud processing. Accept UTF-8 TXT/Markdown transcripts up to 5 MB and WAV/MP3/M4A audio up to 200 MB and 120 minutes. Reject other formats initially. Validate actual MIME/container and decoded duration, not just filename; use bounded decoding, malware checks and quotas. Treat document text as data, never as tool instructions.

Speaker segmentation is best-effort, editable and confidence-labelled. User confirms interviewer/user segments before attributing scores. Import stages are uploaded, validating, transcribing, awaiting review, scoring, completed, failed or cancelled. Retrying a stage must not duplicate attempts. Cancellation stops new provider jobs and deletes temporary material according to the retention policy.

### 6.5 Daily practice and programme

TRN-13: Offer two-minute speech warm-up, five-question communication drill, 30/60-second story retelling, non-technical explanation, social small talk, workplace update, “say it again 20% slower” and end-of-day review. The slower exercise is a relative comparison with the user's previous attempt, not a universal ideal WPM.

TRN-14: Provide morning commute (five technical questions plus one story), ten-minute CV break, twenty-minute deep dive on the journey home and an evening laptop coding/diagram/SQL task. These are editable presets, not assumptions about actual travel or work hours.

TRN-15: Represent the 12-week programme in configuration: weeks 1–2 structure/pace/pauses/fillers/30–45 second answers; 3–4 Story Bank and ownership; 5–6 CV defence and technical follow-ups; 7–8 architecture/distributed trade-offs; 9–10 full interview rounds; 11–12 pressure, ambiguity, interruptions and recovery. Missed days offer a short drill; no punitive streak messages. Replan after user review.

TRN-16: Home shows due topics, story readiness, recent scores, actual practice days, pace trends and one suggested next action. A practice day requires a completed attempt, not opening the app. Notifications default off; when enabled default to at most one reminder per day, quiet hours 21:00–08:00 in the chosen timezone, no sensitive details and one-tap snooze/disable. No exact alarms or continuous activity sensing.

## 7 Communication measurement and coaching

COM-01: Compute metrics from timestamped audio and word-aligned transcripts where available. Text-only sessions can receive content feedback but must show audio measures as unavailable. Never infer actual speaking pace or silence duration from transcript length alone.

| Measure | Definition and handling |
|---|---|
| Answer duration | Monotonic interval from first speech onset to final speech offset; exclude playback and disconnections |
| Overall WPM | 60 × recognized word count / answer duration in seconds; includes internal pauses |
| Articulation rate | 60 × words / detected speech seconds; label separately from WPM |
| Response latency | First speech onset minus end of question playback; no penalty for deliberate thinking pause |
| Fillers | Count and per 100 words; configurable phrases, with contextual review of words such as “like” |
| Pauses | Speech gaps of at least 250 ms; report counts and duration bands, adjustable analysis version |
| Repetition/restarts | Timestamped candidate repeated phrases or abandoned starts; confidence and editable evidence |
| Answer structure | Rubric-based opening clarity, relevance, concise finish, ownership language and run-on observations |

COM-02: Persist provenance (`audio`, `aligned_transcript`, `text_only`, `user_corrected`), units, algorithm version and quality flags. For fewer than ten recognized words or less than five seconds of speech, do not present a stable pace trend. Noise, overlapping speakers, low recognition confidence or missing timing must suppress unreliable measures. If a provider supplies no confidence, mark it unknown rather than inventing a numerical value.

COM-03: Calibrate with three short user recordings and user-selected context. Let users choose pace targets; do not impose one numeric ideal across technical explanation, recruiter call, system design, social conversation and presentation. Use clear recovery phrases such as “Let me structure that clearly” and “The key point is”. Prompt a 1–2 second pause, shorter sentences and replacing fillers with silence. Do not interrupt every minor filler.

COM-04: For the P0 audio pipeline, perform VAD and timing on real audio and obtain word timestamps from the server transcription adapter. Keep waveform/audio in transient buffers unless retention is enabled. If reliable alignment is unavailable, show coarse timing with explicit limits and disable unsupported detailed measures. Validate the algorithm against manually annotated samples, including Nigerian English and different noise conditions.

## 8 Ownership and continuity

SES-01: A logical session stores user, activity/mode, current question/version, selected story/claim, plan position, transcript checkpoint, pending/complete attempt IDs, feedback version, active device and learning context. Resuming does not simply start a new random question.

SES-02: There is one conversational audio owner per user across JARVIS devices, including sessions with different IDs. Enforce with a PostgreSQL transaction/unique user ownership row. Issue a monotonically increasing fencing epoch, 30-second lease and server expiry. Native owner renews every 10 seconds over the authenticated audio connection; acknowledgments include expiry. Stop capture and playback before local lease expiry, allowing a 2-second safety margin based on monotonic time. No silent renewal by an unowned UI or background job.

Every audio stream and state-changing command carries the epoch; Core and devices reject stale epochs. Old output buffers are flushed on revocation. A new owner starts only after the old owner acknowledges stopping or its lease expires. If the old device cannot be reached, target waits rather than allowing overlapping owners. Server-side fencing alone does not silence audio already buffered locally; client expiry enforcement is mandatory.

SES-03: P0 resume fetches the latest checkpoint and asks the current owner to stop if necessary. Persist accepted answers before advancing the question. An unsent partial answer remains local and is labelled interrupted; it does not become a completed checkpoint. A phone may view progress without acquiring audio ownership.

SES-04, P1: Handoff states are requested, checkpointing, target-ready, source-stopped, committed, confirmed, expired or failed. Target readiness includes permissions, network, route and user foreground eligibility. Source checkpoints then stops. Core commits ownership and the new epoch atomically; target starts and confirms. Request timeout is 30 seconds. Before commit, failure leaves/reacquires the source only after validating ownership; after commit, source never auto-resumes. Target failure releases/expires the grant and offers explicit recovery.

SES-05: Persist handoff request IDs and transitions to make retries safe. Resume from a consistent question boundary by default. Mid-sentence audio transfer is not required. Windows/room clients must implement lease/fencing behaviour before P1 handoff is enabled; an Android-only implementation cannot guarantee cross-device exclusivity with legacy clients.

## 9 Offline and synchronization

OFF-01: Cache an explicitly downloaded pack containing a seven-day plan, up to 50 questions, selected story metadata and optional audio prompts. Default cache cap is 100 MB, configurable up to 500 MB. Evict least-recently-used completed cache data; never silently discard pending answers. Show last sync time and download size. Text-only cache is valid if device TTS or downloaded prompt audio is unavailable.

OFF-02: On connectivity loss, stop the live stream, mark an in-flight answer interrupted and persist eligible local draft material. Do not auto-replay raw audio after reconnect; recover at a turn boundary to avoid duplicate provider responses. Show Retry live, Continue offline or End. Offline audio practice may continue only while the existing global lease remains valid. Once expired, offer text-only offline drills; microphone and conversational playback remain stopped until ownership can be confirmed. This intentionally sacrifices fully offline hands-free practice to preserve the one-owner rule.

OFF-03: Offline text answers and consented recordings created during a valid lease are queued for later scoring. Without local-audio retention consent, volatile audio is lost on termination and cannot be recovered; disclose this before practice. Do not claim cloud feedback, transcription, live handoff or adaptive new questions offline. Cache approved feedback, not invented new scores.

OFF-04: Use a transactional outbox with client operation UUID, account/device IDs, entity ID, base revision, payload hash, created time, attempts and state. Server deduplicates operation IDs permanently with canonical attempt IDs; the same key with a different hash is a conflict. Flush on foreground/reconnect; background scheduling is best-effort. Retry transient failures with jittered delays of 1, 2, 4, 8, 16, 30 seconds, then back off to five minutes; honor Retry-After. Do not retry authentication or validation errors indefinitely.

OFF-05: Attempts are append-only; story/preferences edits use optimistic concurrency. On conflict, show server and local versions and let the user choose/merge. Never silently overwrite a story. Session cursor, ownership, score and schedule are server-authoritative. Keep provisional local attempt timestamps for display but use server sequence for canonical order.

OFF-06: Sync supports tombstones and an opaque change cursor. An expired cursor returns `410 cursor_expired`; re-download metadata without losing local pending operations. Logout offers sync, explicit discard, or remain signed in; never move one account's queue to another. Device revocation blocks uploads and refresh, and cached data is cleared at the next authenticated check. Explain that an already-offline device cannot be remotely wiped instantly.

## 10 Permissions privacy and security

SEC-01: Request `RECORD_AUDIO` only when starting voice. Declare `FOREGROUND_SERVICE` and applicable microphone/media-playback service types and permissions. Request notification permission contextually on Android versions that require it. A foreground service must still meet Android notification requirements; if this product cannot expose dependable background controls after denial, restrict to foreground voice and explain why.

SEC-02: Request Bluetooth connection permission only for APIs that require access to connected devices/routing metadata. Prefer system routing and paired devices. Do not scan for peripherals, request location, contacts, phone state, camera, accessibility services, overlay permission or broad storage access. Imports use the system picker. Offer typed drills when microphone access is declined. Respect permanent denial and direct the user to settings without repeated prompts.

SEC-03: Separate live cloud processing consent, local recovery recording consent and server audio archive consent. Default persistent audio retention is off. Active voice requires sending audio to the disclosed configured provider; explain that this is processing even when recordings are not saved. Consent records include purpose, version, time and scope. Revocation ends capture and prevents new retention/jobs for that scope.

SEC-04: Defaults: transient audio buffers up to 30 seconds and cleared after use; opted-in pending local audio deleted after successful upload/scoring or seven days, whichever comes first; opted-in server audio deleted after 30 days; transcripts and detailed attempts after 90 days; Story Bank, goals and aggregate learning records until user deletion. These are configurable product defaults, not legal retention claims. At expiry, pending audio is deleted with a visible failure notice. Recordings must never silently change from temporary recovery to long-term archive.

SEC-05: Provide inspect/export/delete controls for sessions, stories, recordings, memories and account data. Delete returns an operation status, hides data immediately and purges primary objects/derived embeddings/queued jobs within 24 hours. Backups expire within 30 days; restore replays deletion tombstones. Deleting an attempt removes its contribution and recomputes affected aggregates/review schedule. Do not retain personal content in immutable audit logs; keep only minimal operation metadata. Provider-side retention must be disclosed and configured where available; do not promise control beyond the provider contract.

SEC-06: Use PKCE with state/nonce and validated redirects, no embedded credentials. Short-lived access token held in memory; refresh token in secure storage with rotation/revocation. Per-account encrypted local database and app-private encrypted recordings; keys in Keystore-backed storage. Exclude tokens, keys and sensitive cache/audio from Android backup. Logout/discard removes local secrets and files. No hard-coded user identity in API calls.

SEC-07: Authorize every resource and SignalR group by authenticated user; never trust user IDs from request bodies. Device IDs are identifiers, not credentials. Validate ownership for streams and destructive actions. Rate-limit token, stream, import and scoring endpoints. Logs exclude transcripts, raw audio, tokens, signed URLs and CV details. Content tracing is opt-in, redacted and time-limited.

SEC-08: Enforce server-side spend budgets across all devices before opening a provider session and during usage. Require an owner-configured daily currency budget before enabling paid voice; provide configurable session duration caps, 80% warning and hard stop at the reserved budget limit. Meter provider/model usage and reserve worst-case in-flight cost to avoid concurrent overspend. Budget exhaustion offers cached/text practice and preserves completed work.

SEC-09: Treat imported files, transcripts and model output as untrusted content. Models return validated structured assessment data. Mobile coaching has no tool-execution permission. Never turn a phrase in an interview transcript into an instruction to invoke external systems. Audit permission changes, device grants/revocations, handoffs and deletion requests.

## 11 API and event contracts

### 11.1 Conventions

Base path `/api/v1`. JSON uses camelCase, UUID strings, RFC 3339 UTC timestamps, integer milliseconds and explicit nullable fields. Server generates authoritative timestamps; client monotonic durations accompany audio. Authorization is Bearer access token except short-lived, single-use stream grants. Maximum normal JSON body is 256 KB. List APIs return `{items,nextCursor}` with default 25 and maximum 100 items.

Mutating operations accept `Idempotency-Key` UUID and retain operation identity with the resulting entity. Updates use `If-Match` quoted revision/ETag. Return `412 revision_conflict` with current revision for stale updates; `409` for ownership/state conflicts. Use Problem Details with `type,title,status,code,detail,traceId,retryable` and optional field errors. Typical codes: `ownership_conflict`, `lease_expired`, `permission_required`, `budget_exceeded`, `invalid_state`, `unsupported_audio`, `consent_required`, `cursor_expired`, `provider_unavailable`. Never include sensitive content in error detail.

### 11.2 Endpoint catalogue

All paths below are relative to `/api/v1`; `{id}` always scopes to the signed-in user. GET returns 200 unless stated; create returns 201 with Location; asynchronous work returns 202 with `operationId`; delete is idempotent.

| Method and path | Request or query | Response |
|---|---|---|
| GET /bootstrap | appVersion, platform | serverTime, minimumClientVersion, capabilities, limits, consentVersions |
| POST /devices | installationId, name, platform, appVersion, capabilities | deviceId, revision |
| GET /devices | cursor | device summaries and current ownership |
| DELETE /devices/{id} | If-Match | 204; revoke device sessions and credentials |
| GET/PATCH /preferences | patch uses If-Match | locale, timezone, pace targets, reminder and retention settings, revision |
| GET/POST /consents | list or purpose, version, granted | current consent records or created consent |
| DELETE /consents/{id} | none | 204; revoke scope and terminate affected capture/jobs |
| GET /plans/current | none | programme week, ordered due items, plan revision |
| GET /dashboard | from, to | activity counts, scores, quality-labelled trends |
| POST /sessions | deviceId, mode, context, durationMinutes, resumeSessionId? | session snapshot |
| GET /sessions | cursor, status | session summaries |
| GET /sessions/{id} | none | full checkpoint, revision, current attempt and owner |
| POST /sessions/{id}/commands | commandId, command, expectedRevision, ownerEpoch? | updated snapshot; command enum below |
| POST /sessions/{id}/lease | deviceId | leaseId, ownerEpoch, expiresAt or 409 |
| DELETE /sessions/{id}/lease | leaseId, ownerEpoch | 204 after owner stop acknowledgment |
| POST /sessions/{id}/stream-grants | leaseId, ownerEpoch, consentVersion | short-lived stream URL/token and audio config |
| POST /sessions/{id}/attempts | attemptId, questionId, input, timing, ownerEpoch? | accepted attempt and scoring job ID |
| GET /attempts/{id} | none | transcript, metrics, assessment versions, status |
| POST /audio-assets | consentId, sessionId, attemptId, MIME, bytes, sha256 | assetId and upload instructions; P0 recovery audio |
| PUT /audio-assets/{id}/content | binary body, checksum | 204; max 20 MB and 300 seconds, same-content retry safe |
| POST /audio-assets/{id}/complete | checksum | validated uploadId usable in AttemptInput |
| DELETE /audio-assets/{id} | none | 202 deletion operation |
| PATCH /attempts/{id}/transcript | corrected text, If-Match | new transcript revision |
| POST /attempts/{id}/rescore | transcriptRevision | 202 job ID |
| GET/POST /stories | list or StoryDraft | page or created story |
| GET/PATCH/DELETE /stories/{id} | patch uses If-Match | story or 202 deletion operation |
| GET/POST /cv-claims | list or text, sourceRef, storyIds | page or claim |
| PATCH/DELETE /cv-claims/{id} | revision and changed fields | claim or 202 deletion |
| POST /sessions/{id}/handoffs | targetDeviceId, expectedRevision | handoff ID and expiry; P1 |
| POST /handoffs/{id}/actions | ready, sourceStopped, confirm or cancel; epoch/revision | transition snapshot; P1 |
| POST /offline-packs | planId, selectedStoryIds, includeAudio | manifest, versions, checksum and download URLs |
| POST /sync/operations | up to 50 queued operations | per-item accepted/conflict/rejected and server IDs |
| GET /sync/changes | cursor | changes, tombstones, nextCursor |
| POST /debriefs | session metadata, questions, recalledAnswers | review job and debrief ID |
| POST /imports | filename, MIME, bytes, consent, sha256 | uploadId, chunkBytes, expiry; P1 |
| PUT /imports/{id}/chunks/{index} | binary, Content-Range, SHA-256 | 204; repeat same bytes safe; P1 |
| POST /imports/{id}/complete | ordered chunk hashes | 202 processing job; P1 |
| GET/DELETE /imports/{id} | none | status/cancel; P1 |
| PATCH /imports/{id}/speakers | segment assignments, If-Match | confirmed revision; P1 |
| GET /jobs/{id} | none | stage, progress?, resultRef?, error? |
| POST/DELETE /notification-registrations | deviceId, token / registration ID | registration/204; P1 |
| POST /exports | scope, format=json | 202 operation ID, expiring result URL when ready |
| DELETE /sessions/{id} | If-Match | 202 deletion operation |
| DELETE /me/data | confirmed scope | 202 deletion operation |
| GET /operations/{id} | none | pending/running/succeeded/failed, safe error |

Session command enum: `pause`, `resume`, `finishAnswer`, `repeatQuestion`, `skipQuestion`, `end`. Commands that activate audio require valid ownership; pause/end are always accepted from the current owner's device and can also be issued by the authenticated user to stop their session. `resume` alone does not grant a lease. Read-only access never does either.

P0 recovery uploads accept WAV PCM or M4A/AAC captured by the app, verify decoded duration/checksum and existing consent, and are bound to the original session/attempt. They upload already-authorised recordings and do not acquire microphone ownership. Content upload may restart from the beginning after failure; resumable large interview imports remain P1. Recovery assets expire under the local/server audio policy even if processing fails. A recording is never submitted until upload validation completes.

Chunk size defaults to 4 MiB except last chunk. Upload session expires after 24 hours; status reports received chunk indexes for resumption. Finalize verifies hashes, total size and declared limits before processing. DELETE cancels upload and cleans chunks. Signed download URLs expire after five minutes and are not logged.

### 11.3 Representative schemas

```json
{
  "sessionId": "0cdccf86-3165-48df-a2cf-d5c5fbb71912",
  "revision": 18,
  "status": "paused",
  "mode": "cvAttack",
  "context": "technicalInterview",
  "question": {"id": "fx-timeout-01", "version": 1},
  "storyId": null,
  "checkpoint": {"lastEventSequence": 42, "lastAttemptId": null},
  "owner": null,
  "nextAction": "repeatCurrentQuestion"
}
```

```typescript
type AttemptInput = {
  attemptId: string; questionId: string; questionVersion: number;
  input: { kind: 'text'; text: string } |
         { kind: 'stream'; streamId: string; turnId: string } |
         { kind: 'recording'; uploadId: string; consentId: string };
  timing?: { firstSpeechMs: number; lastSpeechMs: number;
             speechDurationMs: number; source: 'native' };
  ownerEpoch?: number; clientCreatedAt: string;
};
type Metric = {
  name: string; value: number | null; unit: string;
  source: 'audio' | 'aligned_transcript' | 'text_only' | 'user_corrected';
  quality: 'valid' | 'low' | 'unavailable'; algorithmVersion: string;
};
type EventEnvelope<T> = {
  eventId: string; sessionId: string; sequence: number;
  type: string; schemaVersion: 1; occurredAt: string;
  revision: number; payload: T;
};
```

Examples illustrate serialization; Codex must produce complete OpenAPI schemas and generated TypeScript clients from the implemented C# contracts before feature integration. UUIDs are required where IDs identify stored entities; question IDs may be stable taxonomy keys. Validate all enum values, lengths and relationships server-side. Text answers max 20,000 characters; story fields max 10,000 each with a 100,000-character aggregate cap.

### 11.4 Control events

SignalR hub `/hubs/sessions` authenticates user and validates membership on `JoinSession(sessionId,lastSequence)`. Methods: JoinSession and LeaveSession; state-changing business actions use REST for consistent idempotency. Durable events include `session.updated`, `question.ready`, `transcript.final`, `attempt.accepted`, `assessment.completed`, `lease.revoked`, `handoff.updated`, `job.updated` and `budget.warning`.

Events are at-least-once; deduplicate by eventId and order by per-session sequence. Partial transcript and audio-level events are ephemeral and not replayed. Reconnect uses bounded retry and token refresh; it does not imply successful session recovery. Fetch durable events through `GET /sessions/{id}/events?afterSequence=N&limit=100`; if retention has expired, return 410 and require a fresh snapshot. Persist canonical event history for 30 days, subject to earlier content deletion. Reconcile snapshot revision before enabling controls. Poll job/session status every five seconds while foreground if SignalR is unavailable, backing off to 30 seconds; no continuous background JS polling.

### 11.5 Audio transport

Use a separate authenticated binary WebSocket `/voice/v1/streams/{streamId}`; do not send base64 PCM through SignalR. A one-use grant expires after 60 seconds and is bound to user/device/session/epoch. Supply it in an authorization header supported by the native client, never in a logged URL. Native service connects directly to Core, not directly to a speech provider.

P0 wire format is PCM signed 16-bit little-endian, mono, 16 kHz uplink and 24 kHz downlink, 20 ms frames. Negotiate and return supported format at stream creation; reject incompatible formats. Each binary frame has a 16-byte header: version byte, direction byte, flags uint16, sequence uint32 and monotonic offset microseconds uint64, all header integers little-endian, then PCM. Uplink frame payload is 640 bytes and downlink 960 bytes. Flags encode end-of-turn and discontinuity; stream direction is 0 uplink, 1 downlink. Sequence starts at zero per direction per stream. Server rejects malformed lengths and oversized frames.

JSON control messages carry `type`, `streamId`, `ownerEpoch`, `commandId` and typed payload: `start`, `turn.end`, `output.cancel`, `heartbeat`, `heartbeat.ack`, `stop`, `error`. The connection is authenticated before any PCM is accepted. Native heartbeat acknowledgments renew the lease in Core. Server returns stream/turn/response IDs for durable linking; `output.cancel` identifies responseId. Deduplicate final turn commit by turnId; a subsequent attempt submission referencing it returns the same attempt.

Bound outgoing queued audio to two seconds. On backpressure, pause and mark discontinuity instead of dropping speech silently or growing memory without limit. Disconnect closes the stream and invalidates its grant; reconnect gets a new grant after lease validation. Resume logical state, never blindly replay old PCM. P1 may negotiate Opus/WebRTC or a provider speech-to-speech adapter after the same auth, spend, consent, ownership and metric contracts pass. Codec/provider changes must not change domain session IDs.

## 12 Data model

Use UUID primary keys, user ownership, UTC creation/update timestamps and numeric revision on mutable entities. Reuse baseline tables where equivalent. Store time-series metrics as typed values with units, not opaque prose only.

| Entity | Required fields and relationships |
|---|---|
| Device | userId, installationId, platform, capabilities, lastSeenAt, revokedAt |
| AudioOwnership | unique userId, deviceId, sessionId, epoch, leaseId, expiresAt |
| Session | userId, mode, context, status, questionId/version, storyId?, planId?, checkpoint, revision |
| SessionEvent | sessionId, unique sequence, eventId, type, minimal payload, schemaVersion |
| Question | stable key, version, domain/tags, difficulty, prompt, expected concepts, rubricVersion |
| Attempt | userId, sessionId, question/version, clientOperationId unique per user, input provenance, status, duration, canonicalAssessmentId? |
| TranscriptSegment | attemptId, speaker, start/endMs?, text, confidence?, revision, provenance |
| CommunicationMetric | attemptId, name, value?, unit, quality, algorithmVersion, source |
| Assessment | attemptId, transcriptRevision, dimension scores/evidence, model/rubricVersion, status, supersedesId? |
| Story | userId, title, baseline story fields, tags, readiness gaps, archivedAt?, revision |
| CvClaim | userId, text, sourceRef, verificationStatus, linked story IDs |
| LearningItem | userId, topic/question/story reference, mastery, dueAt, strongStreak, ruleVersion |
| LearningContribution | unique canonical attempt contribution, prior/new scheduling effect for recomputation |
| ProgrammePlan | userId, timezone, startDate, week, ordered activities, revision |
| Debrief | userId, interview date/context, recalled questions/answers, uncertainty, review |
| Consent | userId, deviceId?, purpose, version, grantedAt, revokedAt? |
| AudioAsset | userId, consentId, attempt/importId, encrypted object key, codec, checksum, expiry, deletion state |
| ImportJob | userId, upload state, chunks, consent, stages, speaker review, retry/error |
| Handoff | sessionId, source/target device, phase, checkpointRevision, epoch, expiry |
| Job and Outbox | deduplication key, kind, payload reference, status, attempts, lease expiry, nextAttemptAt |
| Preferences | userId, retention, voice, pace/context targets, reminders, timezone, revision |
| UsageLedger | userId, provider/model, reservation, actual usage/cost, sessionId, date |
| DeletionOperation | userId, scope, tombstones, progress, requestedAt, completedAt |

Indexes: user/dueAt for learning, user/updatedAt for stories, session/sequence unique, attempt/assessment version, job/status/nextAttemptAt, asset/expiresAt. Enforce user-scoped foreign-key relationships in domain validation and queries. Do not store audio blobs in PostgreSQL. Use transactionally persisted outbox events with mutations. Worker leases jobs and applies bounded retries; dead jobs show a retriable failure to the user. Version local schemas independently and test upgrades without losing pending operations.

## 13 UX flows

Use four tabs: Today, Practice, Stories and Progress. Settings is accessible from the profile control. A persistent active-session strip returns to the voice screen. Use large touch targets, clear text and low visual complexity; avoid a decorative animated assistant that consumes battery.

1. First run: explain companion purpose → configure/select Core → system-browser sign-in → choose timezone and voice → explain cloud processing and separate audio retention → optional microphone test and earbud test → Today. Request microphone only when the test/session begins. Show reachability help if Core is unavailable.
2. Continue: Today → latest checkpoint and next action → acquire lease → route/permission check → start foreground session → question → answer → feedback → next question. Locking the phone preserves the active authorised session and notification controls.
3. Quick drill: Practice → choose two/five/ten/twenty minutes and context → show selected due topic → start. The last turn may finish beyond the chosen time by up to the configured maximum; do not cut off mid-word without warning.
4. Story practice: Stories → select story → readiness gaps or edit → choose retelling length → answer → evidence-based follow-up → review proposed wording → save only after confirmation.
5. Everyday speech: choose recruiter, meeting, presentation, social or technical context → warm-up → short response → one cue → repeat → compare valid audio measures with prior attempt.
6. Debrief: Today quick action or in-session command → recall questions one by one → mark uncertain recollections → review summary → approve follow-up drills.
7. Network failure: immediate Reconnecting banner/notification → stop streaming → retry or cached practice → preserve pending state → reconcile once connected. Never show unsent answers as saved to Core.
8. Handoff: choose target → source saves → target confirms readiness → stop source → Core transfers lease → target resumes question boundary → both show ownership result. Failure states name the next recoverable action.
9. Privacy: Settings → Data and recordings → inspect retention and items → export/delete → explicit scope confirmation → operation status. Deletion does not depend on model availability.

Empty states must distinguish no practice, no due items, permission denied, Core offline, provider failure, processing pending and insufficient audio quality. Every error has a retry, settings or safe-exit path. Do not show a generic spinner indefinitely; after ten seconds show status and cancellation while retaining work.

## 14 Nonfunctional requirements

Targets below are engineering acceptance budgets, not measured results. Record device, build, network, temperature and sample count. Reference testing uses the actual Samsung phone plus one midrange supported Android phone, 100 ms RTT and a stable 10 Mbps connection unless otherwise specified.

| ID | Target |
|---|---|
| NFR-01 | Cold launch to usable cached Today screen p95 under 3 seconds over 30 launches |
| NFR-02 | Tap/media Pause stops microphone and output within 300 ms p95; Stop within 500 ms, measured over 50 commands |
| NFR-03 | End-of-turn to first assistant audio p50 under 2 seconds and p95 under 5 seconds over 50 turns; show processing state immediately |
| NFR-04 | Core non-provider API p95 under 500 ms at 20 concurrent active users; voice latency measured separately |
| NFR-05 | Thirty-minute locked-screen earbud practice completes without lost committed answers or unintended speaker routing |
| NFR-06 | No increasing memory trend over a 60-minute session; provisional mobile PSS budget 250 MB and signed APK budget 100 MB, revisable only with measured ADR |
| NFR-07 | Provisional screen-off battery target at most 8 percentage points per hour on reference Samsung, measured three times with Bluetooth and normal network; report battery capacity and baseline idle drain |
| NFR-08 | P0 PCM total transport data under 12 MB/minute for continuous bidirectional operation; measure actual turn-based usage and display cellular warning |
| NFR-09 | 99.5% Core monthly availability target for hosted deployment, excluding user-controlled laptop downtime; durable accepted attempts survive worker/API restart |
| NFR-10 | TalkBack labels, logical focus, 200% text scaling, 48 dp targets, sufficient contrast and reduced-motion support |
| NFR-11 | No raw audio/transcript/credentials in production diagnostics; automated log scan must pass |
| NFR-12 | Core daily encrypted backup with tested restore; initial RPO 24 hours/RTO 4 hours documented; recent session durability is not equivalent to disaster recovery |

Measure beta crash-free sessions, targeting at least 99.5% with sample size disclosed. Do not claim statistical reliability from a handful of internal runs. For an unreachable personal Core host, show availability truthfully instead of promising a hosted SLA.

## 15 Repository and implementation conventions

Adapt paths to the existing repository while preserving module responsibilities. Proposed layout:

```text
apps/
  Jarvis.Android/
    app/                    # routes and screens
    src/features/           # sessions, coaching, stories, progress
    src/api/                # generated client and transport wrappers
    src/storage/            # database, migrations, encrypted files, outbox
    src/session/            # reducer, recovery, ownership coordination
    src/components/         # accessible shared components
    modules/jarvis-audio/    # narrow Kotlin module and Android service
    plugins/                # repeatable Expo manifest/config changes
    tests/                  # unit, component and device scenarios
    app.config.ts
    package.json
src/
  Jarvis.Api/
  Jarvis.Contracts/
  Jarvis.Orchestrator/
  Jarvis.Voice/
  Jarvis.Memory/
  Jarvis.Coaching/
  Jarvis.InterviewCoach/
  Jarvis.CommunicationCoach/
  Jarvis.TechnicalTrainer/
  Jarvis.Worker/
  Jarvis.WindowsAgent/       # lease adapter only when needed
tests/
  Jarvis.Tests/
  contracts/
  fixtures/audio/           # consented or synthetic, never personal recordings
docs/
  android/                  # setup, permission matrix, test evidence
  adr/
  api/openapi.json
infra/                      # existing deployment conventions
```

Commit generated client output and a CI check that regeneration has no drift. Keep OpenAPI/C# contracts authoritative; do not hand-maintain divergent TypeScript DTOs. Generated Android directories may be regenerated by prebuild; put durable modifications in the local module/config plugins. If the project chooses committed native directories, document that ownership and avoid overwriting changes with prebuild. CI runs reproducible install, lint/type checks, contract checks, tests and a debug/release compile; signing secrets stay in the chosen secret store. Provide `.env.example` with public endpoint/config placeholders only.

## 16 Milestones and Codex execution order

### M0 Integration and audio feasibility

Inspect repository instructions, module/API/auth/database state and target phone/headset. Produce an integration map, pinned stack, permission matrix and audio ADR. Build a real development APK proving 30-minute locked-screen capture/playback, focus handling, routing and media controls with a test service. Confirm encrypted SQLite support. Exit only with measured results or an explicit stack fallback decision; do not hide this behind simulated UI.

### M1 Durable text vertical slice

Implement sign-in, device registry, contract generation, session snapshot, text answer, job processing, Story Bank list/detail, canonical assessment and learning updates. Add ownership store and fake provider fixtures. Exit when the same authenticated user can submit an answer, restart Core/worker and retrieve one canonical result without duplication.

### M2 P0 voice and coaching

Implement native service, stream protocol, permission/focus/route state machine, actual transcription/timing, all configured training modes and post-session summary. Add privacy and spend gates before enabling paid providers. Exit when the earbud voice loop and controls pass real-device tests and metrics show provenance.

### M3 P0 recovery and daily use

Implement local encryption/outbox, downloaded packs, network interruption handling, stop/resume across clients, programme, dashboard, reminders, exports and deletion. Deliver signed test APK, setup/runbook and acceptance evidence. This is the usable P0 release; P1 functionality must remain clearly labelled unavailable.

### M4 P1 continuity and review

Update participating desktop/room clients for fencing and implement live handoff; add consented resumable imports, speaker review, advanced summaries and server notifications. Evaluate full-duplex speech-to-speech and natural barge-in against device/noise tests. Do not enable unsupported full duplex by configuration alone.

### M5 Release hardening

Run load, soak, accessibility, security, battery, migration and backup/restore checks. Resolve critical defects, review dependency licenses and distribution requirements, and produce reproducible build instructions and known limitations. Public store publishing is a separate release action, not implicit in this document.

Codex should work in small end-to-end slices, keep existing tests passing, and report each milestone's changed behaviour and evidence. Never substitute a mock provider for a claimed live integration result. If credentials or hardware are unavailable, finish independent work and mark the exact integration test blocked; do not silently mark the milestone complete.

## 17 Acceptance criteria and tests

| Test | Scenario and pass condition | Release |
|---|---|---|
| AC-01 | Given no microphone permission, opening Today never prompts; starting voice explains/request access; denial leaves typed practice usable | P0 |
| AC-02 | Start on screen, lock phone and complete 30 minutes with Bluetooth; every accepted answer appears once in Core and no unintended speaker audio occurs | P0 |
| AC-03 | Pause/Stop via UI, notification and supported headset meet NFR-02; duplicate commands do not advance twice | P0 |
| AC-04 | Incoming call/focus loss/earbud removal pauses capture and playback; recovery requires explicit action; no call audio is stored | P0 |
| AC-05 | Force-stop during an answer, relaunch and recover last completed question checkpoint; partial answer is labelled, never falsely completed | P0 |
| AC-06 | Submit answer then lose acknowledgment; retry same operation returns the same attempt and exactly one learning contribution | P0 |
| AC-07 | Two devices request ownership concurrently; exactly one grant succeeds; stale epoch cannot capture through Core or mutate live session | P0 |
| AC-08 | Isolate owner from network, wait past lease expiry and acquire on second device; original service has stopped locally before new audio begins | P0 |
| AC-09 | Lose network mid-turn; no unbounded buffer/replayed PCM; offline state truthful; text pack usable; expired lease blocks offline conversational audio | P0 |
| AC-10 | Edit same story on two devices; stale revision yields a merge choice and neither draft is silently overwritten | P0 |
| AC-11 | A text-only answer has null WPM; audio fixture timing within 100 ms and WPM within 10% of manually aligned reference on clean speech | P0 |
| AC-12 | At least 20 consented/annotated speech clips cover accents/noise; filler and restart estimates show measured precision/recall and suppress low-quality results | P0 |
| AC-13 | CV challenge cites actual stored claim; missing metrics remain unknown; proposed story rewrite requires approval | P0 |
| AC-14 | Weak/medium/strong canonical scores schedule 1/3/7 days, second strong 14; corrected assessment reverses prior contribution; skip does not reduce mastery | P0 |
| AC-15 | Every training mode has a configured rubric and runnable test session; drills and story practice use shared history | P0 |
| AC-16 | Audio retention off creates no persistent recording; on creates encrypted asset with expiry; deletion clears primary/derived records within policy | P0 |
| AC-17 | Cross-user requests and hub subscriptions fail without revealing resource contents; expired/revoked tokens cannot stream | P0 |
| AC-18 | Spend reservation rejects new provider work at the limit across devices; completed answers and text/cached practice remain accessible | P0 |
| AC-19 | Reminder opt-out, quiet hours, timezone changes and daylight-saving transitions behave correctly; no duplicate daily notifications | P0 |
| AC-20 | Handoff fails at each transition and recovers without two speakers or lost committed progress; success resumes exact question/story boundary | P1 |
| AC-21 | Interrupted import resumes verified chunks; invalid/oversize content rejected; speaker correction precedes scoring; cancellation removes temporary assets | P1 |
| AC-22 | Barge-in cancels queued output within 300 ms p95 on validated devices; echo is not transcribed as the user's answer | P1 |
| AC-23 | App upgrade migrates encrypted cache with queued operations intact; account switch never exposes previous user's content | P0 |
| AC-24 | TalkBack and 200% font scale allow sign-in, drill, pause/end, story review and deletion without clipping or inaccessible controls | P0 |

Unit tests cover reducers, duration/pace calculations, rubric scheduling, deduplication, budget reservations, consent checks and conflict resolution. Integration tests use a real ephemeral PostgreSQL instance for competing leases, outbox transactionality, jobs, ownership and deletion. Contract tests validate generated clients, Problem Details and events against OpenAPI/schema versions.

Device tests must cover minimum supported API, a recent supported Android release, the user's actual Samsung device, Wi-Fi/cellular transition, airplane mode, screen lock, Doze/battery saver, microphone denial/revocation, notification denial, process death, Bluetooth reconnect and competing media playback. Test audio with hardware; emulators alone cannot validate earbuds or OEM battery restrictions.

Use deterministic fake speech/model providers for most CI tests and a separately budgeted real-provider smoke test. Keep real credentials and personal recordings out of fixtures. Performance/load tests include bounded buffers, stream rejection, slow clients, import decoding limits and worker retries. Security tests include malicious transcript instructions, cross-user identifiers, replayed grants, token leakage, log scans and deletion during in-flight jobs. Release evidence links each acceptance ID to test result, device/build and any documented exception.

## 18 Definition of done and implementation assumptions

The P0 release is done only when M0–M3 and all P0 acceptance tests pass, the user can install a reproducible APK, the Core endpoint/authentication setup is documented, the daily voice loop works on real hardware, and privacy/recovery/spend behaviour is exercised. P1 completion additionally requires M4, all P1 criteria and participating desktop-client changes. M5 is required for a production release. Unimplemented capabilities must not be shown as working buttons.

Assumptions to validate during M0: existing Core uses or can expose the baseline module boundaries; an OIDC-compatible identity flow is available or must be added server-side; a continuously reachable Core host is provisioned for away-from-home use; speech providers can return usable audio/transcript timing; the phone supports the pinned stack; and the user will supply actual CV/Story Bank facts and a paid-API budget. These are integration checks, not reasons to block independent implementation.

Deliver code, migrations, generated contracts, environment example, test fixtures, automated checks, signed test-build instructions, architecture decisions, operational runbook, acceptance evidence and known limitations. Preserve baseline modules and avoid speculative infrastructure. The first implementation action is repository and hardware discovery, followed by the audio feasibility slice.

## 19 Technical references

The stack decision is an engineering recommendation. The following primary documentation was checked on 13 November 2026; verify pinned-version details during M0 and platform rules again before distribution.

- Expo custom native code and development builds: https://docs.expo.dev/workflow/customizing/ — supports the narrow native-module approach; Expo Go is not the target for custom native functionality.
- Expo audio reference: https://docs.expo.dev/versions/latest/sdk/audio/ — evaluate supported non-streaming recording/playback features for the selected SDK; do not assume it provides the full custom streaming service.
- Android foreground service types: https://developer.android.com/develop/background-work/services/fgs/service-types — declare microphone/media playback types and applicable permissions; start capture within platform eligibility constraints.
- Android MediaSession background playback: https://developer.android.com/media/media3/session/background-playback — background service and media-control integration.
- Android audio focus: https://developer.android.com/media/optimize/audio-focus — handle interruptions and applicable foreground eligibility.
- Android Bluetooth permissions: https://developer.android.com/develop/connectivity/bluetooth/bt-permissions — request only permissions required by the selected device APIs.
- ASP.NET Core SignalR JavaScript client: https://learn.microsoft.com/en-us/aspnet/core/signalr/javascript-client — control/event channel and explicit reconnection handling.

The upstream JARVIS Markdown baseline is the product requirements source. These external references support platform constraints; the numeric budgets, wire protocol, retention periods, endpoints and milestones in this document are proposed implementation decisions.
