# B3-5 v0: interview prep pack from a job advert

A first, **deterministic** version of the planned prep pack, built before the multi-step agents (item 6) and per-claim evidence (item 8) that the full B3-5 needs. **No AI, no research, no storage.**

- **Import:** the existing `/api/imports` accepts the new kind `job-advert`: plain text, explicit storage consent, no transcript segments. It follows the existing artifact retention (`ArtifactDays`) like interview transcripts, and deletion uses the existing purge.
- **Pack:** `GET /api/imports/{id}/prep-pack` (owner) runs `InterviewPrepPackBuilder.Build`, which matches the advert against a **fixed, code-owned** list of 14 competencies (C#/.NET, distributed systems, databases, cloud, API design, system design, payments, security, testing, production support, CI/CD, leadership, stakeholders, delivery). For each requirement found it returns the advert sentence it came from (bounded and control-stripped), up to three Story Bank stories that mention the same competency, and whether it's a **gap**. It also gives up to 20 likely questions (one behavioural and one technical per requirement), **gaps first**.
- **Recomputed on read:** deleting the advert or editing stories changes the pack immediately; the pack itself is not stored.

The owner can explicitly create Learning review cards from selected catalogue questions. The browser sends the displayed pack's versioned fingerprint and captured data epoch. Core rebuilds and validates the live source and ordered questions inside the same runtime write as the entire card batch. Changed packs or generations require a reload; missing metadata is refused. Client text is never used as a question or answer. This producer uses an explicit body epoch rather than relying on the current HTTP request's automatically captured epoch.

Cards use the existing learning creation decision and receipts under their original deterministic advert/question request IDs. Repeats return original responses, retired receipts count without returning content or recreating cards, and missing/currently edited cards are not resurrected or overwritten. Concurrent requests count created/existing outcomes within the same lock. A later conflict or cancellation rolls back the entire batch. The fixed answer guide asks for a real Story Bank example and says to report unmeasured results honestly; advert text is not evidence. Generic owner-created cards remain under existing Learning retention after advert deletion, while deleting the advert prevents any further card creation or receipt replay through this endpoint. No new entity, service, history, schema or provider call is added.

## Untrusted text (R2-03)
A job advert is third-party text. It is **only** regex-matched against the fixed list and quoted back to the owner as data, and it's rendered with `textContent` in the browser. It can **never** become:
- context memory: `ContextMemory` now accepts only `cv` imports, where it previously accepted any non-interview import;
- domain knowledge (`DomainFactory` refuses `job-advert` references);
- paid AI assessment input (`AiCoach.ImportedAsync` returns 409; the browser hides the button).

The existing `AgentRuntime.SourceCurrent` import fence also accepts only a live CV with the matching content hash. This closes the manual-domain path: an owner-authored manifest may contain an import reference, but a job advert, transcript or unknown future import kind cannot be used as agent knowledge. `AgentExecutor` checks all selected source references before provider dispatch; an unsupported reference rejects the run before provider calls, usage reservations or results. This protection also applies to later tool/publication source checks and does not depend on using DomainFactory to create the draft. Existing CV context remains available under its existing domain, cloud-consent, privacy and budget gates.

No action is ever proposed from it.

## Not in this slice
Company research, AI-predicted questions, per-claim evidence checks and a generated mock-interview scenario all wait for items 6 and 8, with run cloud consent and the existing budget, exactly as planned.
