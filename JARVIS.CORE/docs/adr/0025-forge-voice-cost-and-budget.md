# Forge voice drills, cost and budget

Status: proposed (F0).

Language and verbal drills are **turn-based by default**. Each turn runs: choose objective, prompt, listen, transcribe, evaluate, give a concise correction, the learner repeats, checkpoint. Transcription, a small text model and TTS go through the existing provider gateway. **Realtime voice is used only when the owner explicitly picks a conversation, mock-interview or public-speaking mode.** The addendum's own illustrative figure, live audio alone at 90 minutes a day costing about $135 a month, shows realtime can't be the default within £100. Those prices are unverified and must be re-checked against official pricing, with an effective date, before they are used.

**Every paid call goes through `UsageBudget`:** a reservation before the turn, reconciliation afterwards, and a cap that holds atomically across agents. Forge has a configurable sub-allowance inside the global ceiling. When the allowance or the global budget is exhausted, the drill offers cached or offline tasks and says why. It never starts a provider call. **No Forge path bypasses the budget.** Tests use fixtures; real-provider smoke tests need owner approval.

Note: the live budget environment is Development (£10, `BudgetPolicy.cs:50`). Paid Forge drills need the owner to move the voice budget to Production (£100) and set the Forge sub-allowance. That is an owner decision, not something code changes.

Audio quality comes before scoring. If audio is noisy, too short or low-confidence, the turn is marked **unscorable** and a retry is offered; it is never recorded as a pronunciation failure. A transcript alone can't establish phoneme accuracy. Calibrated pronunciation scoring is a later adapter (F11), evaluated against human-labelled samples. Accents are preserved: the rubric assesses intelligibility, pace and structure, not conformity to an accent.

Correction mode (immediately, after each turn by default, or at the end of the session) is stored per language profile. Raw practice audio is off by default; saving it follows the consent, encryption and expiry pattern of the interview recordings (ADRs 0019–0021). Capture uses the existing single-owner audio lease, fencing, earbud routing and handoff (ADR 0022), so a drill resumes on Windows at the same checkpoint without a duplicated attempt.

## PF-06 implementation (27 November 2026)

Implemented as an attribution and sub-cap on the existing budget, not a second ledger:

- `UsageReservation.Programme` (optional, closed set: `null` or `UsageProgrammes.Forge`) records attribution on the existing reservation. It is omitted from JSON when null, so existing runtime documents and the privacy export are unchanged for unattributed work. Settlement, mismatch flags and owner reconciliation update the record with `with`, so attribution is retained.
- `BudgetLimits.ForgeMonthlyGbp` comes from `Forge:MonthlyAllowanceGbp`, which is **unset by default**. While it is unset, malformed, non-positive or above £200, every Forge-attributed reservation is refused with 503 before anything is written. Setting it is an owner decision.
- `UsageBudget.ReserveCoreAsync` checks the Forge sub-cap inside the same runtime update as the global GBP ceiling, lane limits, the pause and the reconciliation gates. Forge spend therefore counts toward the global ceiling, and a larger allowance can never raise it. The sub-cap sums this month's same-scope GBP commitments attributed to Forge, with accounted value where settled and the reserved maximum otherwise. Exhaustion returns 429 with code `forge_allowance`.
- No Forge caller dispatches paid work yet. PF-05 drill turns must pass `programme: UsageProgrammes.Forge` on every STT, text, TTS, retry and window reservation, and must handle 503 and 429 by offering offline or unpaid tasks without provider I/O.
