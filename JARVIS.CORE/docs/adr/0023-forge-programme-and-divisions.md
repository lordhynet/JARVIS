# Project Forge programme and divisions

Status: proposed (F0). Owner approval is required before any F1 code.

Forge is a new programme kind beside the existing `ImprovementProgramme`. The 12-week interview programme (`ProgrammeService`, weeks 1–12, one check-in per week) keeps its contract, routes, persisted identifiers and runtime-document storage unchanged. Forge's CAREER division *links* to it: it reads its current week and check-ins and never edits them. Changing `ProgrammeService`'s week rule to fit a 37-week programme was rejected, because it would break existing clients and stored check-ins.

A Forge programme has a local start date, a local graduation date and a timezone, Europe/London by default. Its dates are 1 Dec 2026 to 18 Aug 2027: 261 inclusive programme days, 260 days between the two dates. Timestamps are stored in UTC.
- Before the start the status is NOT STARTED, with calendar progress 0.
- After graduation, progress freezes at 100%, the records are preserved and a successor programme is offered.
- **Calendar progress is never presented as mastery.**
- Phase windows (addendum §15) are versioned configuration, not code branches.

The sixteen divisions (TECH, CAREER, BUSINESS, WEALTH, BODY, HEALTH, MIND, VOICE, PRESENCE, SOCIAL, RELATIONSHIPS, INTELLECT, INFLUENCE, MENTORSHIP, GLOBAL, LIFESTYLE) are **domains in the existing database Domain Registry**, keyed `forge.<division>`, and created as drafts through the registry's own routes. **The owner activates each one**, so a domain definition grants no capability. Targets and cadences are editable versioned configuration, and all of them are candidates, never obligations. Optional divisions such as RELATIONSHIPS stay off unless the owner turns them on, and they never collect quotas or monitor anyone.

The whole feature sits behind `Forge.Enabled`, which is false by default. When it's disabled, the Forge routes return 404 and nothing else changes.
