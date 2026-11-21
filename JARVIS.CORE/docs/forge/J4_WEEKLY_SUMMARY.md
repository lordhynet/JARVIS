# J4: Forge weekly summary

`ForgeWeeklySummary.Build` is a pure projection over **existing** canonical records for the last seven local days ending today (in the programme's time zone). `GET /api/forge/week` is owner-only and Forge-gated (404 when off or until a programme exists). The browser Today panel shows it under "This week".

- **Evidence, counted once each:** learning reviews by `ReviewedAt`, mapped by the existing `LearningDomain` (technical domains → TECH; French and Communication → VOICE; InterviewStory → CAREER); training answers (not skipped or deleted) → TECH; coaching answers (not deleted) → CAREER.
- **Focus divisions** are those with an active tagged goal or any evidence. **Coverage** = practised ÷ focus divisions. When there's nothing to judge it's `null` ("unknown"), never 0. A focus division with no evidence is a real, known 0.
- **No invented time:** `actualMinutes` and `plannedMinutes` are `null`. Minutes aren't tracked yet and planned time needs capacity.
- **Privacy:** counts only. No titles, answers or private values; protected divisions appear only as their code.
- Reads one runtime snapshot (this loads the learning unit, which is acceptable for an on-demand weekly view). No writes, no provider calls.

Not in this slice: spend (needs the Forge budget lane, PF-06), score and evidence weighting (PF-04/PF-10), and next week's proposed plan (the PF-08 caller).
