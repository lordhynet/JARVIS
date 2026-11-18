# Registry-backed turn-based coaching

Four code-authored scenarios (panel, recruiter, presentation, hostile Q&A) extend the existing CoachingCatalog and VoiceCoachingService. Each has personas, three escalating questions and a rubric. They are seeded through the existing domain registry as immutable authored template domains, using current approval/disable/privacy behavior and no new enums. Old installed manifest hashes remain unchanged.

The existing core_coaching tool starts a scenario only from captured unrestricted speech. Each new captured answer is saved by CoachingService, then advances the same VoiceCoaching state to one next authored question. Duplicate answers return the existing state. A persisted domain version and a second active-version/revision check inside D2 fence advancement; disabling the domain cancels active scenario state in the same transaction and keeps answers. Unknown/code-injected scenarios cannot enter manifests. Internal later-turn IDs are not offered as scenario entry points.

The existing realtime stream supplies its actual selected mode to the tool. Scenario progression requires Everyday voice; no routing, price, model, voice profile or budget limit is changed. Questions, escalation and checklist debriefs are authored Core operations with no extra paid call. The phone/browser remain audio clients using the same existing protocol. Optional explicit AI assessment of a saved answer remains its existing separate consented feature.

Debriefs link versioned canonical coaching answers, selected story versions and available captured voice IDs. They include Item 10's metrics for scenario answers only, follow-ups and a bounded English marker checklist. Scores explicitly mean text checklist coverage, not factual verification, expertise or an independently calibrated competence grade. Changed/deleted sources fail closed. Dashboard discovery/debriefs are owner-only and rendered as text. Full erasure/retention reuse the existing lifecycle.

Tests use synthetic speech/provider boundaries and disposable PostgreSQL; hardware coaching quality and live provider acceptance are not claimed.
