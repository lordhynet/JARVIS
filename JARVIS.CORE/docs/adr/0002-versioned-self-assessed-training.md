# ADR 0002: Versioned self-assessed technical practice

Status: accepted, 13 November 2026.

Master requirements sections 6–7 require technical drills and shared learning history.
Provider keys are intentionally absent. Ship a useful text-first workflow with explicit
self ratings rather than presenting synthetic model feedback as evaluated competence.

Reuse canonical ConversationSession IDs and LearningItems. Store bounded, versioned attempts
in the existing runtime document so saves, request deduplication and review recomputation
share one PostgreSQL transaction. Read handlers use IRuntimeReader; command handlers use
TrainingService. C# contracts generate Kotlin DTOs.

Corrections invalidate the current assessment. Deletion purges answer/assessment text.
Recompute mastery from a baseline plus current assessment events and subsequent manual
reviews, ordered by original practice/review timestamps and a common sequence. This avoids
score inflation from historical versions and duplicate replay. Null metrics remain unknown.

The JSON document and finite history caps suit this personal test release; a larger archive
needs indexed attempt/event tables and a retention design. Automatic grading, full catalog,
remaining drill modes, speech metrics and offline recovery are separate future increments.
See [training behavior and API](../TRAINING.md) for exact rules and limits.
