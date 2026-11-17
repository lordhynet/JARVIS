# Guided technical drills — Android 0.6.0

Nine modes now create saved drill plans rather than only assigning a label to an answer. Each plan stores its selected question versions, authored prompts, references, optional code fragments, answer IDs, revision and status. Saving an answer and advancing the plan happen in one locked runtime transaction. Matching retries return the existing answer; two competing saves cannot both advance the same stage. Mixed selections are persisted once, so a retry or restart does not reroll them.

| Mode | Workflow | Current authored coverage |
|---|---|---|
| Rapid drill | One answer with a 20–40 second target | All 16 questions |
| Deep dive | Core question plus two follow-ups | All 16 questions |
| Explain simply | Junior audience, example/analogy and its limits | All 16 questions |
| Architecture challenge | Requirements → design/trade-offs → failure/recovery | Notification service and cross-device ownership |
| Failure injection | Normal path → explicit injected failure | Consumer acknowledgement, outbox relay, command retry, payment ambiguity and device ownership |
| Production incident | Triage → bounded mitigation → verify/learn | Mixed-version schema rollout and release-related latency |
| Code review | Concrete C# fragment → correction and test reasoning | Blocking I/O, scoped lifetime capture, balance concurrency and resource authorization |
| Random mixed interview | Three different domains | Weak questions preferred within a shuffled domain mix |
| Spaced review | One currently due technical question | Existing shared review schedule; no due cards means no fabricated review |

Incident metrics and failures are explicitly fictional exercises. Rapid-drill timing is a target, not measured speech duration. Code is displayed for reasoning and is not compiled or executed. References and saved answers are available for review after answering. Finishing stages does not assign a mastery grade.

## Use and resume

Browser: **Technical practice → Guided drills**, choose a mode and start. Android: **Practice → Guided drills**. Saved plans appear within their canonical session. Resume/reload the plan after reconnecting. Answer, skip without a score, or cancel further stages. Saved answers retain the existing self-assessment, optional consented AI assessment, correction, deletion and shared SRS behavior. AI assessment receives the saved stage's prompt/code/reference rather than an unrelated base prompt.

Android's encrypted queue now also admits unpaid drill creation. Queued stage saves retain the drill ID and step index, in addition to their operation identity and payload. A cancelled/advanced plan produces a visible conflict rather than a silently rebased answer. Local text from a stage that another device advanced is retained for comparison. Paid calls and cancellation are not replayed by this queue.

## API

- `GET /api/training/modes`: mode descriptions.
- `POST /api/training/drills`: `{requestId,sessionId,mode,questionId?}`. A pinned question must have an authored scenario for the chosen mode. Mixed interviews cannot pin a single question.
- `GET /api/training/drills/{id}`: saved plan and non-deleted answers.
- `GET /api/training/sessions/{id}/drills`: latest 30 plans/progress records in a session; answer bodies are omitted to bound payload size. Open a drill by ID to load its saved answers.
- `POST /api/training/drills/{id}/cancel`: `{expectedRevision}`.
- `POST /api/training/attempts`: existing answer contract with optional `drillId` and `stepIndex`. Guided answers must match the active plan's current stage, question, version, session and mode. Existing three-mode legacy requests retain their old fingerprint and enum values.

No new deployment configuration or provider key is required. Core remains on .NET 11 with root `appsettings.json` as its configuration source. This is initial authored coverage of the nine workflows, not the full technical topic corpus, calibrated assessment, code-execution sandbox or physical voice acceptance required by the master document.
