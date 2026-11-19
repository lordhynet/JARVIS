# Forge persistence and review scheduling

Status: proposed (F0).

Forge state goes in **dedicated additive PostgreSQL tables**: programmes, check-ins, capacity, skills, attempts, review states, language profiles, plan items, opportunities and evidence metadata. One migration per slice, as listed in `docs/forge/F0_INVENTORY.md` §4. **Forge adds nothing to `RuntimeDocuments`.** That document already carries Programmes, Learning, Training and Stories, and ADR 0012 records its cold-write cost. Each table:
- is owner-scoped;
- has a `Revision` for optimistic concurrency and UTC `timestamptz`;
- puts a unique idempotency key on anything a client can retry (attempts, check-ins);
- participates in the existing privacy export, erasure and backup.

Writes use the existing transaction boundary and the outbox. Events: ForgePlanChanged, PracticeCompleted, ReviewDue, EvidenceVerified, InterviewApproaching and BudgetThresholdReached. **No second store or orchestration engine.**

Review scheduling is one pure, deterministic function: `next(state, outcome, now) -> state`. It starts with the 1/3/7/14/30-day ladder.
- A failed recall shortens the interval to the previous step, or to 1 day.
- A failure in one skill never blocks unrelated skills.
- Review state is kept per skill and per locale, so French and English never share history.

**Mastery needs both an independent demonstration and a successful delayed recall.** Finishing a lesson never counts. Attempts record accuracy, fluency, independence and retention separately, each with a confidence level and rubric version. A missing observation stays null. An AI-produced rubric score is labelled as such and never presented as an accredited assessment.

The existing `Learning` cards and `TrainingMode.SpacedReview` keep their current behaviour. Forge reuses `TrainingDrillService` and `QuestionBank` for content and calls the new scheduler for Forge skills only. Moving existing cards onto the new scheduler is a separate, owner-approved change.

Rollback: every Forge migration only creates tables, so going down drops them after a verified backup. Nothing else references them.
