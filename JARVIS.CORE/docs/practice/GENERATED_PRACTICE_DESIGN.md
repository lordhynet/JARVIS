# Generated practice questions: engine design (proposed, 28 November 2026)

Status: **PROPOSED, needs design review before implementation.** Slice 2 of `docs/forge/PF05_DRILL_CALLER_DESIGN.md`. Built once in the JARVIS engine for any technical topic; Forge consumes it. Owner decisions: the realtime AI generates questions live, Core tracks them **automatically** (no per-question approval), and authored questions stay.

This answers review findings G1–G4 (`CODEX_REVIEW_GENERATED_PRACTICE_DESIGN_2026-11-28`) with concrete decisions. Language practice and Forge attribution remain behind gates S1–S3 and are out of scope.

## G1: storage, resolver and consumers

### Storage: a new dedicated table, not the training-row codec
Migration **27** adds `GeneratedPracticeQuestions` (its own EF entity and DbSet). It is **not** a new kind in the closed `RuntimeTrainingRows` codec, and it doesn't grow the runtime document.

| Column | Type | Notes |
|---|---|---|
| `Id` | uuid PK | `H("generated-question-v1", sessionId, operationId)`, domain-separated from all other IDs |
| `SessionId` | uuid, indexed | owner/session scope; every read is session-bound |
| `OperationId` | uuid, unique | the generating operation (G2) |
| `Topic` | int | `LearningDomain`; must be a supported topic |
| `OutlineKey` / `OutlineVersion` | varchar(64) / int | authored outline entry it covers |
| `Version` | int = 1 | immutable; never updated in place |
| `Fingerprint` | char(64) | SHA-256 of the complete accepted input (G2) |
| `Json` | jsonb | `{ prompt, referenceOutline, followUps[] }`; null once purged |
| `Provenance` | jsonb | set from trusted Core context only (G3) |
| `Lineage` | jsonb | the context sources present when generated (G3) |
| `Verified` | bool = false | no write path sets true in this design |
| `CreatedAt` | timestamptz | |
| `PurgedAt` | timestamptz, null | text removed; ID tombstone kept |

Indexes:
- `(SessionId, CreatedAt)`
- `(SessionId, Topic, OutlineKey)`
- unique `(OperationId)`

Quotas: ≤ 200 per session and ≤ 5,000 total; beyond that, generation is refused with a clear message. Retention follows the existing session retention.

### Rollback baseline
- **Up:** creates the empty table, with no data movement.
- **Down:** refuses, without mutation, if any row exists. Attempts and cards in the PR45 tables would reference generated IDs that schema 26 can't resolve.
- **Supported downgrade below 27:** only before any generated write, or via an owner snapshot restore with post-backup reconciliation.
- **Tests:** Up/Down on empty; Down refuses with rows; 26 → 27 → 26 empty round trip.

### One resolver
`IPracticeQuestionResolver` (Orchestrator port; in-memory and PostgreSQL implementations):
- `ResolveAsync(sessionId, questionId)` returns a `PracticeQuestion(TrainingQuestion Question, QuestionOrigin Origin, bool Verified)`, or **unavailable** for an unknown or purged ID.
- `ListAsync(sessionId, topic?)` returns authored items (`QuestionBank.All`) plus that session's non-purged generated items.

`QuestionBank` stays the authored definition list, and authored IDs, versions and behaviour are unchanged.

### Consumer matrix (every current `QuestionBank.Get/All` call site)
| Consumer | Behaviour after this change |
|---|---|
| `GET /questions` (TrainingEndpoints:13) | **Authored only** (unchanged); generated items are session-private |
| `TrainingDrillService` topics/pin/candidates (10, 46, 72) | Authored first; generation only via the G2 step; a pinned generated ID must belong to the session |
| `TrainingService.SubmitAsync` (20) | Resolver; generated items are allowed only as the current drill step (G2) |
| `AiCoach` (74) | Resolver; **AI assessment of a generated question is refused with 409 before any provider call** (G4) |
| `CoachingHandlers` session review (67, 76) | Resolver. **Today `Get` would 404 and break the whole review once a generated attempt exists.** Weak domains come from `Question.Domain`, and prompts are shown with their origin label. Purged items show "question removed" |
| `TrainingHandlers` cards list (30) and dashboard IDs (46) | Resolver `ListAsync`, so generated cards appear, labelled; without it they would be silently hidden |
| Learning card creation (inside Submit) | The card copies prompt and reference **plus** `Origin`/`Verified` (G4) |

Drill styles: generated questions support **RapidDrill and DeepDive only** in this slice (prompt plus up to two follow-ups). The other styles need authored fixtures (code fragments, injected faults, incidents); a generated request for them is refused with an explanation, never silently switched.

## G2: loop, replay and tracking

- **Who decides a question is needed:** Core. On `core_practice_start` for a topic, or after a saved answer in a generated drill, Core picks the **least-practised outline entry** (derived from retained attempts) and returns `nextQuestion: { needed: true, topic, outlineKey }`. Authored questions are preferred until each one in the topic has a retained answer in the last 30 days; after that, Core asks for generation. This means fixed authored questions never block new coverage.
- **Generation step:** `core_practice_generate(utterance_id, outline_key, prompt, reference_outline, follow_ups)`.
  - Allowed only when the session's active drill is a generated drill on that topic **and** Core's pending need matches `outline_key`.
  - Operation ID: `Operation("generate", session, utterance)`, where `utterance` is the learner's latest captured speech that triggered the need.
- **Atomic commit:** one store transaction:
  - inserts the immutable question row;
  - appends the drill step (`TrainingDrill.Steps` grows for generated drills; `Revision` increments);
  - clears the pending need.

  A crash leaves either all of these or none. There are no orphan questions and no repeated steps.
- **Replay:** the same operation and identical fingerprint returns the frozen question and step. A changed prompt or reference under the same operation gives 409. The fingerprint covers session, drill, step index, topic, outline key and version, prompt, reference and follow-ups.
- **Learner answer:** saved through the **existing** `core_practice_answer` → `SubmitAsync` path, using the resolver. It requires a **distinct captured user utterance** made after the step was created. The generating utterance and any assistant text are refused, so the AI's own question or reference can never become the learner's answer. The latest-source, restriction and version guards are unchanged.
- **Stop:** `core_practice_stop` or browser cancel ends the loop; generation on a non-active drill gives 409. PR62's resume receipts keep replay bound after a stop.
- **Tracking is derived, not counted:** *generated* = rows in the session; *presented* = drill steps; *answered/practised/last practised* = retained canonical attempts. There's no counter history. Replay, correction, deletion and retention change the numbers only through those sources. **No minutes are recorded** (same rule as P4).

## G3: privacy, lineage and provenance

- **Private from first write:** rows are session-scoped and never enter `QuestionBank`, shared caches or `/questions`. They're included in owner export, removed by whole-Core erasure, and their text is purged (`Json = null`, `PurgedAt` set) when the session is retired by retention.
- **Lineage:** at generation, Core records which context sections were live in the realtime session (e.g. CV, story bank, goals, none) by their existing source references. When any listed source is deleted, restricted or retired (the existing `SourceDeleted` / `ReasoningLifecycle.Retire` scope signals), the question's text, any derived learning-card prompt/reference and cached views are purged in the same canonical transaction. Attempts keep their IDs and mark the source unavailable.
- **Trusted provenance:** `Provenance = { source: "ai-realtime", model, route revision, rate-card date, generatedAt, drillId, captureUtteranceId }`, taken from the session's **Core-held** route and capture context, never from tool arguments. `Verified` is always false. The model cannot attest any of it.
- **Commit fence:** inside the same transaction, Core rechecks the session is active and not retired, the privacy epoch, that the drill is active and the pending need matches, and that the capture is current. A locked/restricted session refuses generation, like other personal tools.
- **Data only:** generated text is stored and rendered as data. It never becomes instructions, tool calls, domains, capabilities or budget changes. Raw text is kept out of logs, audit, outbox and error messages; errors are fixed strings. Bounds: prompt ≤ 1,200 chars, reference ≤ 2,000, ≤ 2 follow-ups of ≤ 300 each, using the existing argument byte/depth limits.

## G4: unverified status everywhere

- `TrainingQuestion` consumers receive `Origin` (`Authored` / `Generated`) and `Verified`. `LearningItem` gains optional `Origin` and `Verified`, omitted for authored items so existing JSON is unchanged. The voice tool results and browser views show "AI-generated question; reference is AI opinion, not verified".
- **Saving an answer creates the existing initial, ungraded technical card** (allowed by the review) labelled generated/unverified. Generation, presentation or answer storage never creates a grade, mastery or SRS advancement.
- **Assessment:** AI assessment of generated questions is refused before any paid call. Owner self-rating stays available and keeps its `self` source label, as with authored questions.
- **Promotion** (optional, later, owner-initiated): creates a new reviewed, versioned authored definition that records its generated origin. It never rewrites the original row, past attempts or references.
- Language practice keeps the separate PF-04 no-card/no-SRS rule.

## Cost
Generation uses the already-open realtime window's tokens; it is not free. Bounded by: the quotas above, a per-session generation limit (≤ 30 per hour), the existing argument limits and the existing global/lane caps. There's no other provider and no PCM replay. Pause or exhaustion ends the window with the existing fallback.

## Implementation slices (after approval)
1. **2a:** migration 27, entity, and in-memory/PostgreSQL resolver; consumer-matrix migration with authored-only behaviour unchanged. No generation yet.
2. **2b:** generation step and atomic commit, replay, loop, answer binding and derived tracking.
3. **2c:** lineage purge and provenance, plus UI/voice labels.

## Test matrix
- **Storage:** migration Up/Down (empty, and refusal with rows).
- **Resolver:** authored equivalence; generated visibility per session; purged → unavailable.
- **Consumers:** every matrix row, including the session review with generated plus purged attempts.
- **Loop:** authored-first then generation; replay and conflicting replay; crash between steps (no orphan); learner answer must be distinct; stop/cancel; quota and hourly limit.
- **Privacy:** lineage purge on source deletion/restriction/retention; erasure; export.
- **Provenance:** untrusted tool-argument provenance ignored.
- **G4 rules:** AI assessment refused before provider; no SRS change from storage; malicious text is inert in prompts and UI.
- **Engine:** locked session refused; no Forge attribution path touched.
