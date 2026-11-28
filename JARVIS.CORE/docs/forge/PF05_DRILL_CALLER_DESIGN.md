# All-purpose realtime voice practice design (proposed, revised 28 November 2026)

Status: **PROPOSED, needs design review before implementation.** This replaces the earlier turn-based French drill-caller proposal in this file.

## Owner decisions (27–28 November 2026)
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
2. **Content breadth.** Many more authored, sourced questions per topic, starting with system design. They are versioned `QuestionBank` entries with references, so there's no AI-generated syllabus.
3. **Languages as topics.** Language practice in realtime (the speaking-conversation mode): extend topics to cover `fr-FR`/`es-ES`/`en-GB` speaking using the merged `ForgeLanguageProfiles` and `ForgeDrillCatalog`. This needs a decision on how language items fit `QuestionBank`/`LearningDomain` without turning SRS on for unscored turns (see #61).
4. **Forge cost attribution on realtime.** While a Forge-programme practice is active, realtime voice reservations pass `programme: forge`, so they count against the owner's Forge allowance inside the global ceiling. Ordinary conversation stays unattributed.

## Cost note (for the owner)
Realtime costs more per minute than turn-based. The existing global caps (£10 development, £100 production, £200 maximum with approval) and the Everyday/Serious voice limits still apply unchanged. Exhaustion ends practice with the existing unpaid fallback. No cap is raised by this design.

## Out of scope
Raising budgets, setting the Forge allowance, live config, deploy, pronunciation scoring, French/Spanish domain activation, and native Android UI.
