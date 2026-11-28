# Practice persistence design (proposed, revised 28 November 2026)

Status: **PROPOSED, needs design review before implementation.** It accompanies the all-purpose realtime voice practice design (PR #58) and replaces the earlier turn-based drill persistence proposal in this file.

## Principles
- **One history:** canonical `TrainingAttempts`. No ForgeAttempts, no second review state.
- **Unscored stays unknown:** never score 0, never a failed assessment, never an SRS advancement from an unscored turn.
- Interview coaching behaviour and statistics are unchanged.

## Technical topics (system design, databases, API security, …): no new persistence
Realtime practice already saves through `core_practice_answer`, which calls `VoicePracticeService.AnswerAsync` and then `TrainingService.SubmitAsync`:
- It's idempotent per captured utterance (`VoicePracticeService.Operation`) and refuses locked-room speech.
- It writes a canonical attempt bound to drill and step.
- It creates the question's learning card and baseline, which is existing, reviewed interview behaviour.

The topic-aware start (#58 slice 1) only changes *which* `QuestionBank` question is chosen. Attempts, cards, export, scoped deletion (PR45), retention and erasure are unchanged. **Nothing to build here.**

## Language topics (#58 slice 3): the only open persistence question
Language items (`ForgeDrillCatalog`, per-locale IDs) are not `QuestionBank` questions, and realtime speaking turns are unscored. Saving them through `SubmitAsync` as-is would break three rules:
1. It would create due learning cards and baselines, which is SRS from unscored turns.
2. It would mix language turns into interview dashboards, weak domains and practice days.
3. It would need a new `TrainingMode` value, and migration 26's **Down** validator refuses `Mode` outside 0–8.

Proposal (unchanged in substance from the earlier revision, now applied to realtime):
- **Contract:** add an optional `TrainingAttempt.Practice: PracticeTurnRecord?` (omitted when null, so interview JSON is byte-identical), holding locale, content key/version, catalogue version and outcome `practice_only`. `QuestionId` is the language item's per-locale ID. `Answers[0]` is the realtime user transcript. `Assessments` is empty.
- **Write path:** a separate `TrainingService.RecordPracticeTurnAsync`, reached from the same realtime answer tool when the active practice topic is a language. It uses the same receipt/replay/fingerprint, the locked-speech refusal and the retired-session checks. It creates **no** learning card, baseline or SRS change.
- **Reads:** interview statistics exclude `Practice is not null`. Forge per-locale practice counts and minutes come later.
- **Privacy:** export, scoped deletion, retention and erasure use the existing mechanisms. Realtime audio is never stored.

## Decisions
- **P1, mode value for language turns:**
  - (a) `LanguageSpeaking = 9` plus a small migration 27 that widens only migration 26's Down mode range;
  - (b) reuse an existing mode plus the `Practice` discriminator, with no migration.
  - **Proposal:** (a).
- **P2:** a saved language turn counts as "practised" (turns and minutes), never "mastered".
- **P3:** clients never write attempts. Core records them from the realtime session.

## Tests required at implementation (language slice)
- Interview JSON is unchanged.
- No card, baseline or SRS change.
- Replay, and a conflicting replay.
- Locked speech is refused.
- A retired session is refused.
- Locale/content mismatch is refused.
- Dashboards exclude language turns.
- Export includes them.
- Scoped deletion works.
- A PostgreSQL round trip preserves the optional property.
- Migration Down behaviour matches the chosen P1.
