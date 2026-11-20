# Shared JARVIS learning storage

JARVIS owns one learning history. Forge uses that history as a project; it does not get its own cards, reviews, baselines or scheduler. This storage slice changes the existing persistence projection, not application contracts or scheduling rules.

The opt-in synthetic baseline on the disposable PostgreSQL server used 1,000 LearningItems, 1,000 LearningReviews and 200 unrelated goals, five warm-ups and fifty pooled samples. Learning-only update p95 was 1308.8731 ms and hot-control p95 44.377 ms. The cold document was 1,985,635 bytes; accessing learning loaded that document. This is a Core/storage workload, not measured voice hardware latency. Full evidence is in outputs/verification-shared-learning/baseline.json.

Extract the cohesive existing learning/replay unit from RuntimeDocuments: Learning, Reviews, TrainingBaselines, TrainingBaselineReviews, TrainingManualSequences and NextTrainingSequence. Goals, attempts and other cold state stay where they are. Their extraction requires its own measured need; do not normalize the entire product to implement Forge.

Use two additive tables in the existing SessionDbContext:
- RuntimeLearningRows: collection kind, canonical dictionary UUID, the existing record JSON, and an optional indexed due timestamp for Learning rows. The composite key keeps collection identities distinct. There is one row per existing record, not one new blob containing the entire learning history.
- RuntimeLearningState: singleton training sequence allocator.

Kinds are a closed persistence vocabulary for the five existing maps, not new product domains/entities. Original JSON preserves exact timestamps, offsets, assessment versions, sources, flags and replay results; the optional PostgreSQL due index never replaces that canonical value. Empty baseline-review sets and every manual sequence remain representable. There are no foreign keys that would discard replay results when a card is deleted.

Existing RuntimeState callbacks keep working. The learning unit hydrates lazily inside the same open privacy/runtime transaction, independently of unrelated cold state and voice. Dirty comparison writes changed records only. Hot audio/control operations do not hydrate learning. Identity snapshots materialize learning before detaching; an unloaded projection cannot run a database loader after its callback closes. The current callback API still loads the cohesive learning unit; this slice does not claim indexed per-card queries throughout the application.

Up backfills and validates identities before removing the six properties from the cold document. Fingerprints are invalidated for recomputation. Down reconstructs the current dictionaries and sequence before dropping tables, including edits/corrections made after Up. Stop all writers, take a verified protected backup and use matching binaries for the storage transition; never mix old/new versions across this migration.

Privacy export, retention, erasure, source retirement, training assessment correction and voice recall continue through the same RuntimeState/ICoreTransaction boundary. There is no second memory, review engine, policy gateway or database. In-memory behavior and public serialization remain compatible.

Acceptance: lossless Up/Down/reapply with sub-microsecond/offset provenance, replay/correction and empty sets; shared-session/runtime transaction rollback; no unrelated cold read/write for learning-only callbacks; no learning read/write during hot controls; privacy/retention/erasure and stale-epoch fences; existing learning/training/voice/coaching tests; controlled before/after measurement and the unchanged hot-runtime p95 under 50 ms gate. No live migration is part of development.
