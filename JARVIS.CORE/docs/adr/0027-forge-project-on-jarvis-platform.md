# Forge project on the JARVIS platform

JARVIS supplies the infrastructure: identity, sessions, coaching, canonical goals and learning, memory, agents, policy, budgets, persistence and clients. **Forge is one persistent project/programme inside that platform.** Its project-specific records describe its calendar, configuration and progress references. They do not establish another assistant, application host, identity, learning history or agent runtime.

The first persistent slice adds `ForgeProgrammes` in the existing `SessionDbContext`. Writes use the existing `ICoreTransaction` and privacy→runtime lock order; PostgreSQL and in-memory implementations stage programme changes with the existing transaction. There is no new database or service process. A read-only port supports CQRS queries. The shared privacy export and erasure include programme records even when Forge is disabled.

The owner requested a plan before substantial architecture changes, then authorized continued building. This slice implements the reviewed programme foundation only. It does not implement or activate the rest of the Forge backlog. Existing twelve-week interview programme contracts, records and active-programme selection remain unchanged. No learning, goal, attempt, memory, ledger or notification copy is introduced.

`Forge.Enabled` defaults to false. A host started with the flag off declares only an owner-authorized 404 boundary, with no functional Forge endpoints; an enabled host checks the flag again at each application entry point, including inside mutations. Enabling routes requires a host restart; disabling them rejects existing calls without deleting stored data. Ordinary JARVIS functionality and the current voice briefing do not depend on this flag.

Programme pause is project-local configuration. It never sets the global JARVIS agent/paid-AI controls. This foundation creates no schedules, domains, paid work or external actions. Future Forge execution must explicitly check project pause and authorization before dispatch; registration alone cannot grant capabilities.

The current Core has a personal-owner boundary. Programme owner scope is assigned by trusted Core code, never by the request body. Composite owner/ID keys and scoped readers prepare the storage boundary without claiming a completed multi-user identity system. All new routes are owner-only; companion credentials cannot access the project setup records.

Configuration v1 fixes the official 1 December 2026–18 August 2027 dates and Europe/London timezone. New dates or successor templates require a compatible configuration version rather than silently changing the current programme. Calendar progress remains elapsed time, not mastery. Pause does not stop the calendar or erase progress.

Deletion retains a bounded configuration-only tombstone to reject stale recreation with the same request ID. Full Core erasure physically clears these records in the privacy transaction. No financial, health, relationship, document or free-text baseline content is accepted by this slice. Those fields require their own reviewed protection/lifecycle design before being added.

Migration `20261120005002_ForgeProgrammes` adds one table and one owner/time index. It does not backfill or rewrite the runtime JSON, existing programme, learning or session tables. Down drops the new table and its project records: preserve a verified backup/export before downgrade. Feature-off is the ordinary reversible way to hide the feature while retaining entered data.
