# Forge persistence and review scheduling

Status: proposed (F0, revised after the gap analysis).

**Forge reuses JARVIS's existing single-product models.** It adds no parallel copies (master requirements, Single-Product Principle):
- Goals are `PersonalGoal`, with a division/skill tag, so they keep driving agent triggers.
- Review state is `LearningItem`. It already has stability, difficulty, `DueAt`, mastery score and source, and a scheduling-rule version.
- Attempts are the existing training and coaching attempts. They gain optional `Locale`, `Assistance`, `Independence` and `Retention` fields.
- Weaknesses come from the existing weakness model.
- An earlier F0 draft proposed `ForgeGoals`, `ForgeReviewStates` and `ForgeAttempts` tables; that was rejected as duplication.

**New additive tables only for concepts with no existing home:**
- `ForgeProgrammes`, `ForgeCheckIns` and `ForgeCapacity` (F1);
- `ForgeSkills` (F2; the skill graph is mapped onto `QuestionBank` and `LearningItem` ids);
- `LanguageProfiles` (F4) and `ForgePlanItems` (F6);
- later, `ForgeOpportunities` and `ForgeEvidence` (metadata only).

Each table is owner-scoped, has a `Revision` for optimistic concurrency and UTC `timestamptz`, puts a unique idempotency key on anything a client can retry, and joins privacy export, erasure and backup. Writes use the existing transaction boundary and outbox.

**`Learning`, `Goals`, `Programmes` and `Training*` currently live in the hot `RuntimeDocuments` JSON** (ADR 0012 records its cold-write cost). Forge would add volume to `Learning` and `Goals`. So a prerequisite slice (FG-00), owned by **all of JARVIS** rather than Forge, moves `LearningItem` (and `PersonalGoal` if measurements show it hot) into dedicated rows. It uses a lossless backfill and a down migration that rebuilds the JSON, following the ADR 0012 procedure. Until FG-00 lands, Forge creates learning items only at the pace of real practice, never in bulk.

**Review scheduling.** It adds the scheduling rule `forge-ladder-v1` beside the existing `manual-interval-v1`. It's a pure function, `next(item, outcome, now)`.
- The ladder is 1, 3, 7, 14, 30 days.
- A failed recall steps back one interval, to a minimum of 1 day.
- A failure never blocks unrelated skills.
- Existing items keep their rule unless the owner moves them.
- Language review state is separated by locale, which is part of the item's domain/skill key.

This is a proposed dispatch rule, not an existing extension point. Today `LearningService.Apply` always invokes `TrainingProjection.Manual`, and assessment replay in `TrainingProjection.Rebuild` can overwrite the rule with `content-score-v1`. F2 must route manual/voice reviews and chronological assessment replay through the same selected rule, retain existing legacy results and request-id replay behavior, and test both training baselines and voice recall. The current `LearningDomain` enum has no locale/skill key; extend it compatibly with optional tags or a versioned mapping, preserving all existing values and serialized records.

FG-00 gates F2's bulk learning creation and ladder, not the standalone F1 calendar. Extraction must retain atomicity between learning items, Reviews and their replay results, training baselines, baseline review IDs, manual sequences, attempts, privacy export/erasure, and session writes under the existing Core transaction. Backfill and Down tests must compare identities, provenance, timestamps and replay behavior as well as counts. Stop all writers for storage-format migration; never mix old/new binaries across it. Measure extraction before considering Goals hot.

Table lists are conceptual sketches, not migration-ready schemas. Specify owner/programme-scoped retry-key uniqueness and request fingerprints (reject a reused key with different content), revision conflicts, foreign keys, deletion ordering and outbox behavior before writing a migration. No sensitive value belongs in an audit or outbox payload.

**Mastery needs both an independent demonstration and a successful delayed recall.** Missing observations stay null, and AI rubric scores keep `MasterySource` showing they are AI-assessed, never accredited.

Rollback: new tables are dropped after a verified backup. Added nullable columns are dropped, and existing rows are unaffected. FG-00 follows its own down migration.
