# PF-04 drill turn persistence design (proposed, 28 November 2026)

Status: **PROPOSED, needs Codex design review before implementation.** Owner decision D3: reuse the canonical attempt path, unscored. Codex guidance (PR53 and #56 reviews): no ForgeAttempts and no second history. Unscored stays *unknown*, never score 0, never a failed assessment, never SRS advancement.

## Constraints found in current source (`development` 1acb751, schema 26)
1. `TrainingService.SubmitAsync` accepts only `QuestionBank` questions and the nine interview `TrainingMode` values.
2. On every non-skipped submit it **creates a due `Learning` card and a `TrainingBaseline`**. For a language drill turn that would add spaced-repetition reviews, and the unscored rule forbids that.
3. Interview dashboards (`TrainingDashboard`, weak domains, practice days) read all attempts. Language turns would distort interview coaching statistics.
4. Migration 26's **Down** validator refuses `Mode` outside 0–8. Any new mode value makes rolling back 26 refuse, which is safe (refused, not corrupted) but a real rollback consequence.
5. Attempts live in `RuntimeTrainingRows` as JSONB. New **optional** properties need no SQL migration; the validators ignore unknown keys and the codec keeps them.

## Proposal
### Contract (additive, omitted when null)
Add a trailing optional `TrainingAttempt.Practice: PracticeTurnRecord?`, `JsonIgnore(WhenWritingNull)`, so interview attempts serialize byte-identically:
```
PracticeTurnRecord(string Locale, string ContentKey, int ContentVersion, string CatalogueVersion,
                   string QualityRuleVersion, string Outcome /* "practice_only" | "scored" */,
                   double ActiveSpeechSeconds, string TranscriptionModel)
```
- `QuestionId` = `ForgeDrillItem.Id` (the per-locale review identity). `QuestionVersion` = item `Version`.
- **Mode:** append `TrainingMode.LanguageSpeaking` (= 9). This is decision P1 below.
- `Answers[0]` = the transcript text with source `"voice_transcript"`. `Assessments` = empty. `CanonicalAssessmentId` = null, so `TrainingProjection` already ignores it.
- Raw audio is never stored, as today.

### Write path
A new `TrainingService.RecordPracticeTurnAsync(...)`, separate from `SubmitAsync`, so the interview path is unchanged. It reuses the same receipt, replay, fingerprint, retention and session checks inside `store.UpdateAsync`.
- **No `Learning` card, no `TrainingBaseline`, no SRS change.**
- Validates the item from `ForgeDrillCatalog` (locale, key, version) and the locale from `ForgeLanguageProfiles`.
- Persists only **clean** outcomes: practice-only now, scored later. Retry outcomes (silence, too short, wrong language) stay ephemeral.
- **Idempotency:** `RequestId` = the deterministic turn ID (also the reservation ID). The fingerprint is (session, locale, key, version, transcript, rule version, outcome). Same input replays the same record; a different payload with the same ID gives 409.
- **Ordering:** the drill caller calls this only after the final `current` fence. The record is written in the same unit of work as nothing else, and a stale result is never written.

### Read paths
- **Interview statistics:** dashboards, weak domains and practice days exclude `Practice is not null`, so interview coaching is unchanged.
- **Forge:** later, a separate read for language practice counts and minutes per locale. Not in this slice.

### Privacy and lifecycle (existing mechanisms only)
- Export already includes attempts, so locale and content metadata appear in the owner's export.
- Deletion uses the merged scoped `DeleteTrainingAttemptAsync`: answers cleared, `DeletedAt` set, reasoning retired (harmless).
- Retention: existing session retirement and `ArtifactDays` apply. Erasure removes all rows.

## Decisions for Codex and the owner
- **P1, mode value:**
  - (a) append `LanguageSpeaking = 9`. Honest, but migration 26 Down refuses such rows until a follow-up migration widens its validator;
  - (b) reuse `RapidDrill` plus the `Practice` discriminator. Rollback-compatible, but it overloads an interview mode.
  - **Proposal:** (a), with a small reviewed migration 27 that widens only the Down validator's mode range. Rows stay lossless.
- **P2, completion:** a persisted practice-only turn counts as "practised" (minutes and turns), never "mastered".
- **P3, Android/Windows clients** never write attempts directly. Core records the turn after the fence. Android stays audio-only.

## Tests required at implementation
- Interview attempt JSON is unchanged.
- No Learning card, baseline or SRS change.
- Replay and conflicting replay.
- Retired session.
- Catalogue or locale mismatch.
- Dashboards exclude practice turns.
- Export includes them.
- Scoped deletion works.
- A PostgreSQL round trip through the PR45 codec preserves the optional property.
- Migration Down behaviour for the chosen P1.
