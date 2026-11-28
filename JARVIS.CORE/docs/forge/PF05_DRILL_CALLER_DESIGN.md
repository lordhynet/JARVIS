# All-purpose realtime voice practice design (proposed, revised 28 November 2026)

Status: **PROPOSED, needs design review before implementation.** This replaces the earlier turn-based French drill-caller proposal in this file.

## Owner decisions (27–28 November 2026)
- **JARVIS is the engine:** practice is built once in the core engine for any topic; Forge is a programme on it.
- **All-purpose:** one voice practice for any skill, started naturally, e.g. "Hey JARVIS, can we practise system design now?". Languages are topics in the same system, not a separate drill.
- **Realtime everywhere:** practice runs on the **existing realtime voice session**. This supersedes ADR 0025's turn-based default.
- **Audio-first clients:** first the browser main window on the owner's Windows PC, then the Windows tray companion after install and a microphone check, then Android.
- **Persistence:** canonical attempts, no parallel history (see the PF-04 design, PR #61, which is to be revised to match).

## What already exists (reuse, don't duplicate)
- Realtime voice tools `core_practice_current/start/answer/stop` (`Jarvis.Api/Voice/CoreVoiceTools.cs`) call `VoicePracticeService`, which uses `TrainingDrillService` and `TrainingService.SubmitAsync`, which saves canonical attempts.
- Fencing, the audio lease, the paid pause, the budget, the locked-session restrictions and handoff are already enforced by the realtime session.
- `QuestionBank` has 16 authored interview/technical questions across 8 `LearningDomain`s (2 each). `DrillCatalog` defines 9 drill styles (`TrainingMode`).
- Merged Forge pieces stay available: language profiles, French content, the quality gate, and turn-based transcription. They are off the main path under the realtime decision. Nothing is deleted.

## Gaps and proposed slices
1. **Topic-aware start (first).** `core_practice_start` takes only a drill *style*. Add an optional **topic** (`LearningDomain`) through `StartTrainingDrillRequest` and `TrainingDrill`. It's an additive optional field, omitted when null, and part of replay identity. `TrainingDrillService` filters candidates by topic. If a different topic is already active, the user is asked whether to stop it (409), never switched silently. The tool description maps natural requests ("system design", "databases", "API security"…) to topics, and chooses a fitting style (system design suggests `ArchitectureChallenge`; "quick" suggests `RapidDrill`). If a topic has no content for that style, it says so rather than substituting another topic.
2. **Content breadth: AI-generated questions, tracked autonomously** (owner decision, 28 November 2026). **Concrete design: `docs/practice/GENERATED_PRACTICE_DESIGN.md`** (answers G1–G4). This is built in the JARVIS engine, topic-agnostic; Forge only consumes it. The realtime AI writes the question live; Core records it automatically, with no owner approval step. Authored `QuestionBank` questions remain and are preferred where they exist.
   - **Topic outline (grounding):** a small authored, versioned outline per topic lists competencies and named sub-areas (e.g. system design: requirements, data ownership, consistency, scaling, failure and recovery). The tool requires the generated question to name one outline entry, which steers coverage without authoring every question.
   - **Tool:** a `core_practice_start` variant (or a sibling `core_practice_generated`, chosen at review) whose arguments carry the generated `question`, the chosen outline entry and a short `reference_outline`. Core validates the bounds (length, topic, outline membership, no URLs or credential-like text) and treats the text strictly as **data**, never instructions.
   - **Storage:** a canonical **generated question** record: deterministic ID from (session, utterance), topic, outline entry, text, reference outline, source `ai-realtime`, model ID and route, `Verified = false`, created-at. Its location (runtime JSON collection, or rows beside the PR45 training rows) is a storage decision for review. It is exported, erased, retention-bound and scoped-deletable with its attempts.
   - **Attempts:** the spoken answer saves through the existing `core_practice_answer` path to a canonical attempt bound to the generated question. `QuestionBank.Get` is extended behind one resolver (authored or generated), not by duplicating `SubmitAsync`.
   - **Tracking (autonomous):** per-topic and per-outline-entry counts of questions practised and answers saved, plus last practised, are kept automatically. A review card for a generated question is created like any attempted question, labelled unverified.
   - **Honesty rules:**
     - no grade or score without a real assessment;
     - the AI's reference outline is always presented as "AI opinion, not verified";
     - generated content is never relabelled as authored;
     - a later owner action may promote a generated question into the authored bank (optional, not required).
   - **Cost:** generation happens inside the already-paid realtime window, with no extra provider call. The existing budget caps apply.
3. **Languages as topics.** Language practice in realtime (the speaking-conversation mode): extend topics to cover `fr-FR`/`es-ES`/`en-GB` speaking using the merged `ForgeLanguageProfiles` and `ForgeDrillCatalog`. This needs a decision on how language items fit `QuestionBank`/`LearningDomain` without turning SRS on for unscored turns (see #61).
4. **Forge cost attribution on realtime.** While a Forge-programme practice is active, realtime voice reservations pass `programme: forge`, so they count against the owner's Forge allowance inside the global ceiling. Ordinary conversation stays unattributed.

## Cost note (for the owner)
Realtime costs more per minute than turn-based. The existing global caps (£10 development, £100 production, £200 maximum with approval) and the Everyday/Serious voice limits still apply unchanged. Exhaustion ends practice with the existing unpaid fallback. No cap is raised by this design.

## Out of scope
Raising budgets, setting the Forge allowance, live config, deploy, pronunciation scoring, French/Spanish domain activation, and native Android UI.

## Gates before language or Forge-attributed practice (from design review, 28 November 2026)
Slices 3 (languages) and 4 (Forge attribution) are **blocked** until a revised design satisfies each gate below and is reviewed. Technical-topic practice (slices 1–2) keeps the existing global-budget path and isn't affected.
- **S1, Forge admission across realtime windows:** decide whether the whole window is classified at admission, or a transition closes the old window and reserves a new Forge-attributed window before any Forge audio or work.
  - Never relabel settled usage.
  - Cover start/stop, prewarm, renewal, mode/provider fallback, reconnect and handoff.
  - Missing allowance, pause or exhaustion refuses before further Forge provider work and keeps unknown-outcome holds.
- **S2, programme authority:** trusted owner/programme identity and revision, plus enable/pause/delete checks at admission and again inside the canonical write transaction.
  - A surviving audio lease never authorizes a stale programme.
  - Project pause is local to Forge.
  - An unpaid fallback has no automatic retry and no new IDs.
- **S3, language eligibility and persistence:** language entry stays blocked until canonical mapping, locale/content/source versioning, quality evidence, no-baseline/no-SRS behaviour and interview-statistics exclusion are reviewed.
  - Realtime-generated text is never pronunciation evidence or a score.
  - Accents are preserved.
- **PF-04 persistence (#61) prerequisites:**
  - **P1:** an explicit rollback baseline and a concrete Up/Down algorithm, because migration 26's Down can't be widened by a later migration. Test 27 → 26 → 25.
  - **P2:** a consumer/lifecycle matrix for practice-marked attempts (views, resume, correction, assessment refused before any paid assessor call, projection, deletion, retention, export, erasure), plus a catalogue-aware content lookup.
  - **P3:** a Core-owned capture envelope with an atomic source/privacy/programme fence immediately before persistence.
  - **P4:** practice minutes stay unknown until a reviewed per-capture measurement exists.

#61 is **held** until it is revised to this realtime/canonical flow.

## Proposed resolution of S1 and S2 (design only; for review)
Grounded in current source: `RealtimeVoiceSession.ReserveWindowAsync` (one reservation per window, made before the window's provider work), the existing successor renewal (`window_renewing`, prepared in the background and dispatched only after handoff), `UsageBudget.ReserveCoreAsync` (the Forge sub-cap is checked under the same lock as the global ceiling; unsettled charges count at their maximum), and `ForgeProgramme(Id, OwnerScope, Revision, ConfigurationVersion, …, Paused, DeletedAt)`.

### S1: classify whole windows; switch at a window boundary
- **Rule.** Attribution belongs to a window and is fixed when that window is reserved. It is never changed afterwards. Settled or unsettled usage keeps the label it was reserved with.
- **Start inside an open global window.** `core_practice_start` for a Forge programme does not run Forge work in the current window.
  - Core records a pending Forge binding (S2) on the conversation and returns `forge_admission_pending`.
  - The session ends the current window through the existing renewal path, and reserves the successor with `programme = Forge` before any Forge audio, generation or capture.
  - Until a Forge-attributed window is live, Forge tools refuse with 409, so the model can't do Forge work under the global label.
- **Stop.** Stopping Forge practice clears the binding. The current Forge window stays Forge-attributed until it ends, because it is never relabelled. The next window is reserved as global.
- **Prewarm and renewal.** Every successor reservation re-reads the binding and runs S2 admission. If admission fails (no allowance configured, allowance exhausted, programme paused or deleted, or revision changed):
  - the successor is reserved global, or not at all if the global budget also refuses;
  - the Forge drill ends with a recorded reason;
  - Forge tools refuse;
  - no automatic retry and no new Forge request IDs.
- **Mode and provider fallback.** A fallback route's reservation carries the same programme attribution and re-runs admission. The Serious daily cap and the global ceiling still apply, because the Forge sub-cap can only refuse.
- **Reconnect and handoff.** The binding is conversation state, not device state. The new device's first window runs admission from the stored binding. The old device's in-flight results bind to its own capture and route as before, so they are refused as stale.
- **Unknown outcomes.** Existing holds stay counted at their maximum in both the global and Forge sums. Admission doesn't release or re-label them.

### S2: programme authority bound and re-checked
- **Binding.** Add `ForgeBinding(ProgrammeId, OwnerScope, ConfigurationVersion, AdmittedAt)` on the conversation's practice state.
  - Bind `ConfigurationVersion`, not `Revision`, because `Revision` changes with ordinary check-ins. The binding comes from the owner-authorized programme read; the model never supplies it.
- **Admission check, inside one composite transaction** (`ICoreTransaction`, which already reads `ForgeProgrammes`):
  - the programme exists with matching owner scope;
  - `DeletedAt` is null and it isn't `Paused`;
  - its `ConfigurationVersion` equals the bound value;
  - the conversation isn't retired, the device isn't locked, and the privacy epoch is current.
  - The reservation is then written in the same transaction, which needs a composite-writes reservation method, so a pause or delete can't race between the check and the reservation.
- **Canonical writes** (practice answers, generated questions) run the same programme check inside their existing store transaction. A stale programme can't write merely because its audio lease survived.
- **Pause is local to Forge.** A paused programme refuses Forge admission and Forge writes only. Technical practice, ordinary voice and global budgets continue unchanged.
- **Delete** clears bindings in the programme-deletion transaction, so later windows are global.

### Tests required before slice 4 is built
- Start inside a global window: the Forge window is reserved before any Forge tool succeeds, and the old window's reservation keeps its global label.
- Stop: the current window keeps the Forge label, and the next window is global.
- Renewal while paused, deleted, at a changed configuration version, or with the allowance exhausted: the successor is global or refused, the drill ends with a reason, and there is no retry.
- Fallback route: same attribution, admission re-run.
- Handoff: the new device's admission uses the stored binding, and the old device's late writes are refused.
- Pause racing a reservation, and pause racing an answer save: no Forge reservation or write after the pause commits.
- Unknown-outcome holds are counted in the Forge sum.
- No paid provider call anywhere; fixture sockets only.

S3 (languages) is unchanged by this revision and stays blocked.
