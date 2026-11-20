# Shared learning query backlog

JARVIS owns one learning/replay history; Forge adds project, skill and locale tags to it (PF-04). Keep LearningDomain stable. Divisions and locales are not new enum entries or new card stores.

FG-00-Q is required before PF-05 bulk review volume when either the card count exceeds 2,000 or isolated learning-update p95 exceeds 250 ms at 1,000 cards. The current 424.4127 ms synthetic measurement already triggers this work; it is scheduled, not deferred until after launch growth.

Acceptance for that slice:

- Async per-card and bounded due-page reads use the existing rows and (Kind, DueAt) index without hydrating unrelated learning history. Record returned/read counts and query plans on synthetic 1,000/2,000/10,000-card fixtures.
- Preserve the existing privacy epoch, owner scope, transaction lock, request replay and assessment-correction semantics. Multi-map writes remain atomic; no second scheduler, cache authority or history.
- Canonical JSON remains authoritative for timestamp offsets/ticks. Recheck the exact due timestamp after the coarser PostgreSQL index filter, including boundary cases.
- Existing learning/replay/privacy regressions pass. The controlled hot-runtime p95 remains below 50 ms; target isolated single-card updates below 250 ms on the same measured fixture. Record mixed-workload/maintenance contention separately.
- Replace synchronous lazy reads together with these async query ports (review L4); do not move sync database work to Task.Run or weaken the shared transaction boundary.

The existing cold/learning callback API remains compatible until those ports are adopted. Missing baselines stay unknown; project pause must be honoured by future Forge dispatch.
