# JARVIS delivery scope

The original delivery scope below is implemented. The newer
[master requirements](JARVIS_MASTER_REQUIREMENTS_SINGLE_PRODUCT_2026-11-13.md) govern further development.
This expands the original README scope
into concrete acceptance criteria, rather than assuming unspecified native apps exist.

1. Session core: authenticated REST/SignalR, checkpoints and lifecycle.
2. Persistence: PostgreSQL migrations, restart survival and concurrent writes.
3. Devices and handoff: registration, heartbeat, verified capabilities, checkpointed
   transfer, source release before target ownership, expiration and recovery.
4. Learning: persistent cards, due queue, four review ratings, review history and
   deterministic scheduling. This is a simple interval scheduler, not trained FSRS.
5. Tools and integrations: audited, confirmed Home Assistant light/switch actions;
   Google/Microsoft calendar reads; live TfL line status; partial-failure briefings.
6. Conversation and voice: activity-specific coaching through OpenAI, persisted
   transcripts, microphone capture, cloud transcription, browser speech output,
   explicit foreground wake-word mode and stop control.
7. Companion and operation: responsive browser client, configuration status,
   database readiness, container deployment, backup/restore and scenario checks.

Live acceptance needs configured provider credentials and actual devices. Mock HTTP
tests establish request/response behavior, not account access, acoustic accuracy,
physical device operation or background/mobile browser reliability. The browser companion
is the original runnable client. A native Android personal test build is now available in
the sibling `JARVIS.MOBILE` folder; Quest and satellite firmware remain absent.

## November continuation

Android 0.3.0 adds communication/interview exercises, source-backed CV defence,
versioned coaching answers, deterministic text observations, current session reviews,
a twelve-week programme and explicit weekly self check-ins. The browser now includes
these modules plus Story Bank editing, technical practice and workflow controls.
See [full coverage and remaining work](IMPLEMENTATION_STATUS.md) and [coaching semantics](COACHING.md).

Android 0.2.0 adds a text-first technical training increment: 16 questions, three practice
configurations, versioned saved answers and self assessments, correction/deletion, shared
1/3/7/14-day scheduling and honest practice-day/domain progress. See [training scope](TRAINING.md).
This completes an initial drill-history slice, not the entire technical trainer or automatic grading.

Story Bank backend: authenticated create/list/read/update/delete, all section 5.1 fields,
incomplete drafts, validation, retry-safe creation and version-checked edits/deletion.
Stories use the existing transactional runtime document and survive restart with PostgreSQL.
No fabricated stories are seeded. Native/browser editors and source-backed coaching are implemented.

Remaining P0 work includes the real-time voice loop, communication metrics, interview modes,
full technical catalog, automatic multidimensional weakness scoring and post-session scoring.
The original learning cards and activity-specific chat prompts only partially cover these requirements.

The next Core slice adds durable practice-plan workflow runs, cancellation, claim fencing,
bounded retries and replayable status events. See the [integration ADR](adr/0001-unified-product-and-workflow-foundation.md)
for exact coverage and pending agent/mobile gates. Native Android uses Kotlin/Compose
per the master; the older Android v1.0 React Native decision is superseded on this point.

The Android APK is built, signed, installed and connected on the user's Samsung. It adds
session/story/review/workflow screens and turn-based native recording controls. The paid
Claude draft adapter has consent and atomic budget gates, tested with fixture responses.
Neither this APK nor the adapter completes the full mobile/agent release; see
`JARVIS.MOBILE/README.md`, `docs/PAID_AI.md` and verification evidence for remaining gates.
