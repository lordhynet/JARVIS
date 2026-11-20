# Project Forge F0: repository inventory, mapping, migration plan and backlog

Status: proposal for review (revised after the gap analysis, claude-review/JARVIS_FORGE_GAP_ANALYSIS_2026-11-19.md). Docs only; no feature code.
Base: `development` at `edaa3c7` (release 0.3.1 tree `a953a825`).
Requirements: the owner's `JARVIS_PROJECT_FORGE_MASTER_ADDENDUM_V2.md`. It stays separate from the master requirements until the owner approves merging (addendum §14).
Plan: `claude-review/JARVIS_FORGE_INTEGRATED_PLAN_2026-11-19.md`.
Decisions: ADRs 0023–0026 in this folder's sibling `docs/adr`, plus `JARVIS.MOBILE/docs/adr/0005-android-stack-for-forge.md`.

Every fact below was checked by reading the repository at `edaa3c7`. Anything marked *unverified* was not inspected.

## 1. Actual structure

Core has four projects, not the separate assemblies the addendum names (`Jarvis.Coaching`, `Jarvis.TechnicalTrainer`, `Jarvis.InterviewCoach`, `Jarvis.CommunicationCoach` and others):

| Project | Relevant folders |
|---|---|
| `Jarvis.Api` | `Endpoints/` (21 endpoint groups), `Voice/CoreVoiceTools.cs`, plus `AgentWorker`, `CalendarTriggerWorker`, `WorkflowWorker`, `RuntimeWorker`, `CqrsRegistration`, `Integrations/` (provider gateway) |
| `Jarvis.Contracts` | `Coaching.cs`, `Training.cs`, `Learning.cs`, `Planning.cs` and others (DTOs and records) |
| `Jarvis.Orchestrator` | `Coaching/`, `Training/`, `Domains/`, `Intelligence/`, `Runtime/`, `Workflows/`, `Sessions/`, `Recordings/`, `Travel/`, `Cqrs/` |
| `Jarvis.Persistence` | `SessionDbContext` with 20 migrations, the latest being `20261119000007_InterviewRecordings` |

Clients: `JARVIS.MOBILE` (Kotlin/Compose), `JARVIS.WINDOWS` (tray companion, local wake, governed actions) and `JARVIS.FRONTEND` (browser).

## 2. Addendum concepts mapped to existing code

| Addendum concept | Existing code (verified) | Notes and reuse |
|---|---|---|
| 12-week interview programme | `Coaching/ProgrammeService.cs`. The contract types are `ImprovementProgramme`, `ProgrammeWeek`, `ProgrammeCheckIn` and `ProgrammeView` (`Contracts/Coaching.cs:34-39`). | Weeks are hard-limited to 1–12 (`ProgrammeService.cs:38`), with one check-in per week. It's stored in the runtime document (`Programmes`, `ActiveProgrammeId`). **Do not change it** (ADR 0023). |
| Technical trainer | `Training/TrainingDrillService.cs`, `QuestionBank.cs`, `DrillCatalog.cs`, `TrainingService.cs`, `VoicePracticeService.cs` and `TrainingProjection.cs`. `TrainingMode` has 9 values, including `SpacedReview` and `FailureInjection` (`Contracts/Training.cs:4`). | `SpacedReview` selects `Learning` cards where `DueAt <= now` (`TrainingDrillService.cs:29-38`). This is the start of the review ladder (ADR 0024). |
| Learning and memory | `Runtime/LearningService.cs`, `Contracts/Learning.cs` (cards with `DueAt`), `Intelligence/ContextMemory`, `ContextSelector` | Learning cards live in the runtime document's `Learning` section. |
| Interview coaching and communication | `Coaching/CoachingService`, `VoiceCoachingService`, `CoachingScenarios`, `CoachingCatalog`, `SpeechDelivery`, `TextCoach` | Pace, filler and delivery measurement already exist (ADR 0010). **There is no locale or language-profile concept anywhere.** |
| Story Bank | `Api/Endpoints/StoryEndpoints.cs` and the runtime document's `Stories` section | Reuse as is. |
| Domain registry | `Orchestrator/Domains/`, which includes `DomainRegistryService`, `DomainFactory`, `DomainPolicy` and `CapabilityCatalog`. Persistence rows: `DomainDefinitionRow`, `DomainVersionRow`, `DomainCapabilityBindingRow`, `DomainApprovalRow`, `DomainAuditEventRow` and `UserDomainStateRow`. | **Routes:** `POST /domains/proposals`, `POST /domains/drafts`, owner-authorised activation, `POST /domains/{key}/disable`, `GET /domains/{key}/state` (`DomainEndpoints.cs:14-27`). The Forge divisions use this (ADR 0023). |
| Orchestration and agents | `Workflows/AgentRuntime`, `AgentPlanning`, `AgentSpecialists`, `AgentToolGateway`, `PracticePlanProcessor`, `WorkflowService` | Due-review reminders and later research tasks. Nothing new is needed. |
| Budget | `Workflows/BudgetPolicy.cs`. `Environment` is "Development" or "Production" (`:20`). The Development ceiling is £10 (`:50`); Production is £100, higher only with approvals (`:48-50`). `UsageBudget` and `UsageReconciler` handle reservations. | Every Forge provider call goes through here (ADR 0025). |
| Daily brief | the `core_briefing` tool in `Api/Voice/CoreVoiceTools.cs` (`4e6f763`) | It gets the daily top three (F1). |
| Calendar | `Api/CalendarTriggerWorker.cs` | Verified in follow-up: reads entries, filters interview titles and ingests/cancels durable preparation triggers. It does not calculate free/busy capacity. F6 needs that adapter. |
| Sessions, audio and handoff | `Runtime/` (`CompanionVoiceService`, `AudioOwnership`, `AudioFencing`, `CompanionHandoffService`, `SessionLifecycleService`, `WakeCandidateArbiter`) | Single audio owner, fencing, phone/Windows handoff (ADR 0022) and wake arbitration. |
| Consented audio | `Orchestrator/Recordings/`, `InterviewRecordingRow`, `RecordingChunkRow` (ADRs 0019–0021) | The pattern for consent, encryption, expiry and budgeted transcription that Forge practice audio follows. |
| Privacy and deletion | `Runtime/DataPrivacy`, `MemoryPrivacyStores`, `Api/Privacy`, `PrivacyControlRow`, `PrivacyJobRow` | New Forge tables must join export and erasure (ADR 0026). |
| Android | `JARVIS.MOBILE`, Kotlin/Compose, 0.11.3 (18) | The addendum's React Native mention is resolved by the mobile ADR 0005. |
| Windows screensaver V1/V2 | *Not found in this repository at `edaa3c7`; unverified.* | Locate the source before F11 (addendum §18). |
| Wear OS / Galaxy Watch7 | None | F11. |

## 3. Gaps

| Gap | Slice |
|---|---|
| A programme longer than 12 weeks, with a calendar, phases, NOT STARTED and frozen-at-100% states | F1 |
| Programme divisions with editable targets and cadences | F1 (domain registry) |
| Typed-in check-ins for body, health and wealth, plus capacity settings | F1 |
| A read-only display snapshot API | F1 |
| A skill graph with prerequisites, rubric version and evidence type | F2 |
| Separate accuracy, fluency, independence and retention scores with confidence | F2 |
| Per-locale language profiles and drills (en-GB, fr-FR, es-ES) | F4, F5, F9 |
| A turn-based (non-realtime) voice drill path | F4: `VoicePracticeService` already saves canonical captured answers into the existing training history with deterministic IDs/replay guards. It does not provide the transcription/text/TTS pipeline, language profiles or audio-quality scoring. Reuse its history and session boundary. |
| A deterministic capacity planner and replanning rules | F6 |
| Evening and weekly reviews, and scores that show coverage | F6 (the weekly summary is block-3 item 5) |
| A sandboxed technical assessment | F7 |
| Opportunity and evidence registers, and the financing gate | F8 |
| An encrypted evidence vault | F10 |
| Screensaver and watch | F11 |

## 4. Migration plan (all additive, one migration per slice)

| Slice | Migration | Tables | Backfill | Down |
|---|---|---|---|---|
| F1 | `ForgeProgrammes` | `ForgeProgrammes` (Id, OwnerScope, Version, LocalStartDate, LocalGraduationDate, TimeZone, Status, Revision, CreatedAt, DeletedAt), `ForgeCheckIns` (Id, ProgrammeId, Division, LocalDate, Kind, ValueJson, IdempotencyKey unique, CreatedAt), `ForgeCapacity` (ProgrammeId, WeeklyMinutes, QuietHoursJson, SleepWindowJson, Revision) | None | Drop tables. No other table references them. |
| F0b (all JARVIS) | `LearningAndGoalRows` (FG-00) | Move `Learning` (`LearningItem`) and, if measured hot, `Goals` (`PersonalGoal`) out of `RuntimeDocuments` into dedicated rows. **This is not Forge-only;** it's the one learning history and goal model. | Lossless backfill from the JSON | Rebuild the JSON from rows, then drop. Same procedure as ADR 0012. |
| F2 | `ForgeSkills` | `ForgeSkills` (Key, Division, Version, PrerequisitesJson, RubricVersion, EvidenceType), mapped onto existing `QuestionBank` and `LearningItem` ids. **No Forge attempt or review table:** attempts reuse training and coaching attempts (with an optional `Locale`, `Assistance`, `Independence` and `Retention`), and review state reuses `LearningItem` with the `forge-ladder-v1` scheduling rule. | None | Drop the table, and the added nullable columns, after a backup. |
| F4 | `ForgeLanguageProfiles` | `LanguageProfiles` (Locale, SkillBandsJson, CorrectionMode, Revision) | None | Drop. |
| F6 | `ForgePlans` | `ForgePlanItems` (Id, ProgrammeId, GoalKey, WindowStart, WindowEnd, Minutes, Status, Reason, Revision) | None | Drop. |
| F8 | `ForgeRegisters` | `ForgeOpportunities`, `ForgeEvidence` (metadata only) | None | Drop. Back up first, because the owner enters data. |

Every table is owner-scoped, has a `Revision` for optimistic concurrency, uses UTC `timestamptz`, and is included in privacy export and erasure. **Nothing goes into `RuntimeDocuments`** (ADR 0012 hot-path risk).

These column lists are sketches: owner scope/revisions omitted above are still required. Final schemas must define programme-scoped retry uniqueness, request fingerprints and mismatch rejection, FK/deletion order and transaction/outbox semantics (ADR 0024). FG-00 is required before F2 learning volume, independently of F1's pure calendar; its backfill also covers Reviews, baselines, sequences and replay provenance. Do not add a second learning history.

## 5. Backlog (F1 split into reviewable commits)

1. Contracts and migration `ForgeProgrammes`, plus the pure `ForgeCalendar`: phase, elapsed days, NOT STARTED, frozen at 100%, with Europe/London dates and 261 inclusive days. **Tests:** 30 Nov, 1 Dec, 31 May, 18 Aug, 19 Aug, and both daylight-saving changes.
2. `ForgeProgrammeService`: create, read and update, with idempotent create. Creating it never touches `ImprovementProgramme`. **Test:** the existing 12-week programme view is unchanged.
3. The division seed: 16 domain drafts through the existing domain registry, with **owner activation** required. They carry no capabilities that execute anything.
4. Check-in and capacity endpoints under `/api/forge/*`, with idempotency keys. Plus **privacy export and erasure for the new tables.**
5. The daily top three in `core_briefing`. They're rule-based: due reviews first, then goals, with a reason shown for each.
6. The read-only `GET /api/forge/snapshot` (addendum §18 fields, plus `privacyMode`), a browser Today view and the spoken Android briefing. The current owner instruction is audio-only Android: no native Today dashboard or offline business answer queue. A screensaver reads only a sanitized local CurrentUser snapshot written by Windows, with no credential/network access.
7. A `Forge.Enabled` setting, **off by default**. Every Forge route returns 404 when it's off.

Build the flag before registering any Forge routes. The standalone calendar draft has no routes, persistence, provider calls or production activation. Its phase configuration is provisional until the original owner addendum is available for independent verification; keep that addendum separate from the master requirements.
