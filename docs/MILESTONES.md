# JARVIS delivery scope

The README is the agreed scope (there is no separate milestone plan). This expands it
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
physical device operation or background/mobile browser reliability. Native Android,
Quest and satellite firmware are not present; their common protocol is available to
clients and the browser companion is the runnable client in this repository.
