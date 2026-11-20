# PF-08 core: deterministic day allocator

`Jarvis.Orchestrator.Forge.ForgeDayAllocator.Plan` is a pure function. It reads an authorised `ForgeCapacityProjection` (already composed with JARVIS quiet hours through `WithPolicyQuietHours`) plus up to 64 candidate activities, and returns a `ForgeDayPlan`. It performs no storage, clock read, provider call, calendar write or agent dispatch, and adds no table, route, contract or DI registration.

## Guarantees (tested in `ForgeDayAllocatorTests`)

- **Unknown, paused and zero capacity plan nothing.** Status `CapacityUnknown`, `ProgrammePaused` or `NoCapacity`, and every candidate is deferred with that reason. Unknown is never treated as free time.
- **Never over capacity.** Planned minutes are at most `AllocatableMinutes` (net of fixed blocks and buffer).
- **Real windows only.** Items are placed in open windows: the local day minus the projection's fixed blocks (owner work and sleep plus unioned policy quiet hours), minus anything before `notBefore`. No item overlaps a fixed block or another item. An item is never split across a block. If no window fits even its minimum, it is deferred with `NoOpenWindow`.
- **Real time.** Windows are computed in instants with explicit offsets, so 23- and 25-hour clock-change days plan correctly.
- **Deterministic.** Allocation *selection* order is: due reviews, then interview practice when an interview is within 7 days, then other work, then optional work; within each, priority, `DueBy` and key (ordinal). The same input in any order gives the same plan. This is not a chronological promise: an intact long higher-ranked item can land in a later window while a lower-ranked item fills an earlier gap. `DueBy` is a soft sort key, not a hard latest-completion constraint; hard deadline feasibility belongs to the candidate/planning integration.
- **Private by type.** `ForgeDayPlan`, `ForgePlannedItem`, `ForgeDeferredItem` and `ForgePlanCandidate` refuse JSON in both directions (the same converter as `ForgeCapacityProjection`), because occupied windows, minutes, deadlines and keys derive from private capacity. Owner views and exports must be separate, minimised projections; never log, cache, emit or send these to agents, providers or unattended snapshots.
- **Display offsets.** `Start`/`End` are correct instants, but `End` keeps `Start`'s offset; owner displays must convert through the programme time zone.
- **Edge inputs.** A `notBefore` at or after the day's end (including `DateTimeOffset.MaxValue`) yields no windows instead of overflowing.
- **Interview urgency.** Within 24 hours of an interview, optional work is deferred with `InterviewWithin24Hours`. Sleep and work blocks are never touched by urgency.
- **Every unplaced task has a reason** (`BudgetExhausted`, `NoOpenWindow`, `InterviewWithin24Hours`, or the capacity status). There is no punitive backlog; the caller decides whether a deferred task moves to another day.

## Not in this slice

- **The caller owns preconditions:** resolve fresh owner capacity and current quiet-hours policy, compose them with `WithPolicyQuietHours`, and fence programme revision and privacy epoch before acting on a plan. PF-08 integration isn't complete until that caller exists.

- Building candidates from due `LearningItem`s, `PersonalGoal`s and drills (PF-04 tags), and the "replan at most daily unless significant change" policy: that's the caller.
- Commute audio windows (the capacity model has no commute block kind yet).
- Briefing top three and Today view (PF-09).
- Any calendar write, which still needs exact owner approval.
