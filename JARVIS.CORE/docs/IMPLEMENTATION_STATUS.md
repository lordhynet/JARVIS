Owner direction on 14 November supersedes earlier Android feature screens. Android is audio only; business capabilities stay on Core. See [architecture decision](adr/0002-audio-only-android-core-owned-product.md). Full voice-driven feature actions and away-from-home connectivity remain incomplete.

# Master requirements coverage — audio-only Android 0.10.0 / .NET 11

This inventory records implemented increments, not acceptance of the full master scope.
The same distinction appears in `/api/modules` and the browser All modules
panel. Planned capabilities are not represented as working implementations.

| Module | Available now | Remaining |
|---|---|---|
| Sessions / memory | PostgreSQL history, canonical sessions, checkpoints and derived reviews | OIDC/user isolation and complete retention/deletion |
| Voice | Realtime gateway, streamed browser/native audio, barge-in, current-owner fencing, transcript persistence and Core practice tools | Additional voice feature workflows; live provider, acoustic and hardware acceptance |
| Android / earphones | Audio-only Kotlin/Compose companion, encrypted connection, Core-owned conversation preparation, foreground live microphone and headset Stop/interrupt controls | Physical offline/process-death recovery; earbud, screen-off, interruption and battery acceptance |
| Communication | Eight exercises, versioned answers and explicit text observations | Acoustic pauses/restarts, calibrated speech feedback, hands-free progression |
| Interview / CV | Sixteen structured exercises, real-story/claim prompts, follow-ups and debrief | Adaptive questioning, verified factual/technical evaluation, full interview sequencing |
| Technical trainer | Sixteen questions/eight domains, nine guided modes, authored multi-stage scenarios and self/AI assessments | Full topic catalog, broader scenarios, calibrated adaptive evaluation and code execution |
| Story Bank | All current fields and browser/Core CRUD, text CV import/source-quote provenance, linked coaching deletion | Binary document extraction and automatic coaching |
| Weakness / learning | Self-rated domains, shared due cards, persisted voice recall with explicit spoken ratings, correction-aware 1/3/7/14-day scheduling | Evaluated multidimensional scoring and scheduler calibration |
| Twelve-week programme | Calendar-aware plans and explicit weekly self reflections | Reminders and adaptive replanning |
| Post-session reviews | Current counts, questions, observations, self-rated domains and next drills | Evaluated automatic transcript scores and measured speech summaries |
| Cross-device continuity | Shared Core IDs, checkpoints and practice histories on browser/Android | Realtime audio continuity and offline reconciliation |
| Agent runtime | Durable runs/events, aggregate-backed typed specialist tasks, leases, schedules, quiet-hour inbox and shared budgets | Dedicated specialist task tables, reconciliation UI and calendar/event triggers |
| Interview preparation workflow | Unpaid plans, consented Claude drafts and source/budget gates | Calendar/event triggers, specialist orchestration and automatic delivery |
| Browser companion | Existing sessions/devices/integrations plus six new feature panels | Complete accessibility/user acceptance and offline recovery |
| Calendar / transit / home | Existing adapters, partial failures, confirmed allowlisted actions and audit | Real account/hardware acceptance and proactive delivery |
| Real-interview archive | R1 encrypted recording/capture and R2 quote-confirmed budgeted transcription into the existing import store; track labels, owner relabelling, partial/reconciliation recovery | R3 recording-derived practice; physical capture/lock/recovery/transcription acceptance; precise word timing and phone diarization remain unavailable |
| Research / review specialists | Budgeted public search, citation artifacts and separate review tasks | Claim-level independent source verification and live provider acceptance |
| Career / personal planner | Consented planning briefs, daily schedules, persistent Core goals and browser/Core editing, review tasks | Broader adaptive planning and policy-gated external tools |
| Privacy / data lifecycle | Core export, confirmed atomic erasure, request/cache generation fences, observed-erasure cleanup on Android, retention preview/application and disabled-by-default scheduler | Fully offline propagation, per-record device retention, backup/provider copy lifecycle and broader provenance/import |
| Quest / room voice | Explicitly planned | Native/firmware implementation and hardware acceptance |

Paid providers remain disabled unless configured. See [coaching scope](COACHING.md),
[training scope](TRAINING.md), [paid-AI gates](PAID_AI.md) and [verification](VERIFICATION.md).

Android implementation resumed at the owner’s request. Version 0.7.0 adds privacy-generation
checks, stale-request rejection and local cache cleanup on observed Core erasure or revoked
access. Fully offline propagation and individual-record retention remain separate limits.
See [PRIVACY.md](PRIVACY.md); verification is recorded separately.


Current Core voice tools and remaining priorities are tracked in [CORE_BACKLOG.md](CORE_BACKLOG.md) and [LIVE_VOICE.md](LIVE_VOICE.md).


