# B3-5 v0: interview prep pack from a job advert

A first, **deterministic** version of the planned prep pack, built before the multi-step agents (item 6) and per-claim evidence (item 8) that the full B3-5 needs. **No AI, no research, no storage.**

- **Import:** the existing `/api/imports` accepts the new kind `job-advert`: plain text, explicit storage consent, no transcript segments. It follows the existing artifact retention (`ArtifactDays`) like interview transcripts, and deletion uses the existing purge.
- **Pack:** `GET /api/imports/{id}/prep-pack` (owner) runs `InterviewPrepPackBuilder.Build`, which matches the advert against a **fixed, code-owned** list of 14 competencies (C#/.NET, distributed systems, databases, cloud, API design, system design, payments, security, testing, production support, CI/CD, leadership, stakeholders, delivery). For each requirement found it returns the advert sentence it came from (bounded and control-stripped), up to three Story Bank stories that mention the same competency, and whether it's a **gap**. It also gives up to 20 likely questions (one behavioural and one technical per requirement), **gaps first**.
- **Recomputed on read:** deleting the advert or editing stories changes the pack immediately; nothing derived is stored.

## Untrusted text (R2-03)
A job advert is third-party text. It is **only** regex-matched against the fixed list and quoted back to the owner as data, and it's rendered with `textContent` in the browser. It can **never** become:
- context memory: `ContextMemory` now accepts only `cv` imports, where it previously accepted any non-interview import;
- domain knowledge (`DomainFactory` refuses `job-advert` references);
- paid AI assessment input (`AiCoach.ImportedAsync` returns 409; the browser hides the button).

The existing `AgentRuntime.SourceCurrent` import fence also accepts only a live CV with the matching content hash. This closes the manual-domain path: an owner-authored manifest may contain an import reference, but a job advert, transcript or unknown future import kind cannot be used as agent knowledge. `AgentExecutor` checks all selected source references before provider dispatch; an unsupported reference rejects the run before provider calls, usage reservations or results. This protection also applies to later tool/publication source checks and does not depend on using DomainFactory to create the draft. Existing CV context remains available under its existing domain, cloud-consent, privacy and budget gates.

No action is ever proposed from it.

## Not in this slice
Company research, AI-predicted questions, per-claim evidence checks and a generated mock-interview scenario all wait for items 6 and 8, with run cloud consent and the existing budget, exactly as planned.
