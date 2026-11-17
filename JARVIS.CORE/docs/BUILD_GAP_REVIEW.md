> Historical 0.3.0 review. Since this review, 0.4.0 adds the realtime gateway and browser/native streaming clients, optional AI assessment, and aggregate-backed specialist tasks with schedules, quiet-hour inbox and shared budgets. See IMPLEMENTATION_STATUS.md and VERIFICATION.md for current scope and evidence. Remaining acceptance gaps below have not all been resolved.
# Build-gap review — 13 November 2026, version 0.3.0

Scope: compare the master requirements with source, endpoint/project inventory, Android
manifest/services, and recorded verification. This was a review; no new acceptance tests
or live provider/hardware checks were run. Existing evidence records 118 backend,
26 browser and four Android unit tests for implemented increments.

## Entire capabilities still missing

- Realtime voice gateway/transport, streamed replies, VAD/turn handling and barge-in.
- Dedicated native Windows agent; only the browser companion currently covers Windows UI.
- Consented real-interview audio archive/import pipeline, speaker segmentation,
  question extraction and evidence-based post-interview analysis. A text debrief exists.
- Research and Review specialist execution with claim-level source evidence.
- Career/personal-planner workflows, durable authorised goals and associated schedules.
- Quest client and dedicated room/far-field voice endpoint/firmware.
- CV document import and structured claim/evidence provenance.

## Partial implementations requiring substantial work

| Priority | Area | Missing behavior |
|---|---|---|
| P0 | Spend/policy gateway | Global reservations for OpenAI chat, transcription and future realtime voice; lane budgets, timed voice reservations, provider routing/fallback policy and reconciliation tooling. Existing Claude-brief ledger is narrower. |
| P0 | Audio ownership | Global expiring/fenced conversational audio ownership and local playback expiry during disconnects. Existing session ownership/handoff is not this complete protocol. |
| P0 | Android continuity | Persistent active checkpoint/question/transcript drafts, offline mutation queue and conflict recovery; current drafts/pending IDs are process-memory only. |
| P0 | Android live state | SignalR/WebSocket subscriptions, reconnect/resume and prompt/progress/ownership events; current native client primarily uses REST and foreground polling. |
| P0 | Android hands-free control | MediaSession/headset buttons, explicit Bluetooth route management, native local VAD/wake-word where practical and continuous hands-free coaching progression. A turn-based foreground microphone service exists. |
| P0 | Identity/security | OIDC Authorization Code + PKCE, user-scoped authorisation and device identity; current personal build uses a shared Core key. |
| P0 | Coaching | Adaptive questioning, evaluated technical/factual scoring, actual timestamp-supported speech metrics, context-sensitive delivery feedback and full mock-interview sequencing. Current authored prompts, lexical observations and self ratings are explicitly limited. |
| P0 | Technical training | Full topic catalog; six missing modes: Deep Dive, Architecture Challenge, Failure Injection, Production Incident, Code Review reasoning and Random Mixed Interview. Some coaching prompts share names, but the technical trainer lacks those mode implementations. |
| P0 | Agent orchestration | Specialist registration, persisted typed tasks/handoffs, authenticated occurrence-based triggers, durable schedules, notification outbox, quiet hours/freshness, per-workflow/global pause and full call/tool/concurrency caps. Runs, leases, retries and per-run cancellation already exist. |
| P0 | Proactive interview preparation | Calendar/event-driven briefs, rescheduling, specialist coordination and automatic delivery. Current briefs are manual, consented requests. |
| P0 | Privacy/memory | Configurable retention jobs, full account/memory export/deletion, provenance/review state and propagation to all derived stores/caches. Paid derived-content physical purge within 24 hours is pending. Existing resource deletes cover specific records. |
| P1 | Programme/proactivity | Practice reminders, adaptive replanning, daily/weekly summaries, missed-practice recovery and activity-aware notifications. Calendar plans and self check-ins already exist. |
| P1 | Home/lifestyle | Timers, household routines, cooking/baking guidance and practical-planning flows beyond current calendar/transit adapters and allowlisted Home Assistant actions. |
| P1 | Operations | OpenTelemetry queue/lease/provider/cost/voice-latency metrics, restart/sleep/power-loss recovery acceptance, unattended continuous-host rollout and validated restore objectives. |

## Built but not yet accepted in real use

- Android 0.3.0 is installed, but its new screens, fresh connection, earbud routing,
  screen-off behavior, interruption handling and battery behavior need native acceptance.
- Paid Claude/OpenAI quality, real usage/cost reconciliation and voice latency need live
  configured-provider evaluation. Missing keys do not account for the separate unbuilt
  gateway, realtime, orchestration and scoring capabilities above.
- Calendar/Home Assistant account/device integrations need actual configured acceptance.
- Seven-day soak, clean-environment restore, WAN interruption and latency/concurrency gates
  remain acceptance work. Backup files and existing scenario tests provide partial evidence.
- Browser responsive functional tests and coaching visual review passed; complete
  accessibility and broader user acceptance remain pending.

## Suggested build order

1. Global provider-spend/policy controls and audio ownership/recovery foundation.
2. Realtime voice plus native live-state transport and durable reconnect/offline state.
3. Evaluated adaptive coaching/scoring and the complete technical drill modes.
4. Trigger/schedule/task/outbox orchestration and the first automatic interview workflow.
5. Complete retention/provenance controls, operational instrumentation and acceptance gates.
6. Archive, research/review, career planning, Windows agent and additional hardware clients.

Identity hardening must precede multi-user/external deployment. Changes should retain the
existing canonical Core data and deliver real behavior rather than additional status screens.

Sources: master requirements sections 2.2–2.4, 3–16 and 19–24;
`src/Jarvis.Api/Endpoints`, `src/Jarvis.Orchestrator`, Android manifest/services/ViewModel,
`docs/PAID_AI.md`, `docs/COACHING.md` and `docs/VERIFICATION.md`.
