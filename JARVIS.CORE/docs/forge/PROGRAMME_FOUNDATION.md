# Forge programme foundation

Forge is a project inside JARVIS. Core owns the programme state; existing JARVIS services supply its infrastructure. This slice is programme configuration and calendar persistence, not the full adaptive development loop.

## Configuration and routes

`Forge.Enabled` is false by default. The sanitized example includes that key; this change does not modify deployed private settings. Start an authorized local/test host with it enabled to register the following functional owner-only routes:

| Method | Route | Behavior |
|---|---|---|
| POST | `/api/forge/programmes` | Create with a nonempty requestId; optional date/timezone fields default to the official programme. Same ID/configuration returns the existing record. Different configuration or a deleted ID conflicts. |
| GET | `/api/forge/programmes` | List retained active project records with their current calendar view. |
| GET | `/api/forge/programmes/{id}` | Read programme configuration and calendar position. Deleted/unknown IDs return 404. |
| PATCH | `/api/forge/programmes/{id}/preferences` | Set `paused` with `expectedRevision`. A stale revision conflicts; a no-op keeps the revision. This pauses the Forge project only. |
| DELETE | `/api/forge/programmes/{id}?revision=...` | Revision-checked deletion; keeps a configuration tombstone. It does not delete the interview programme or shared learning. |

This first configuration supports the official dates only: 1 December 2026 through 18 August 2027 inclusive, Europe/London. There are 261 inclusive days and 260 between the dates. The programme is NOT STARTED before day one; calendar progress is separate from mastery, and freezes at 100% after the final day. The May 31 checkpoint and both clock changes are covered by the existing calendar tests.

Dates, timezone, revision, project pause and timestamps are the only persisted programme content. Owner scope comes from Core. No public/companion setup permission, raw audio, private baseline data, agent schedule, grant, provider invocation or budget change is created.

Restart Core after changing Forge.Enabled from false to true. Disabling an enabled host takes effect without restart and retains project data. Include this restart and post-restart checks in the launch activation checklist.

## Transactions and privacy

The programme table shares JARVIS's database and transaction/privacy gates. Failed composite writes roll back programme and runtime changes together. Concurrent hosts serialize create/revision checks through the existing runtime lock. Queries use the read-only programme port. Programme configuration never enters `RuntimeDocuments` and does not change the legacy interview ActiveProgrammeId.

Export includes stored programme configuration and deletion tombstones when present, including while the feature is disabled. When no Forge data exists, the new optional export field is omitted. Full Core erasure clears the table and in-memory state, including tombstones; stale privacy generations cannot recreate data. Programme configuration follows owner deletion rather than automatic session/answer retention. The retained tombstone has only configuration metadata. The ten-programme limit counts active programmes only; deleting a programme does not consume that allowance forever or require erasing JARVIS to free a slot. Tombstones still reject stale recreation until full Core erasure.

The additive migration creates only `ForgeProgrammes`. Down drops that table and its data; restoration requires the protected backup/export. Up/Down/reapply tests verify the existing interview/runtime JSON stays unchanged. No live migration or feature activation is part of this implementation.

## Remaining work

The next slices attach canonical goals/divisions, capacity and protected baselines, then canonical practice/locale/review references and the first French-to-Windows loop. Shared learning, budgets, policy and clients remain JARVIS facilities. This foundation does not claim adaptive planning, language scoring, research, offline Forge packs or physical device acceptance.
