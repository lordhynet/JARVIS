# J4: weekly recorded-practice summary

`ForgeWeeklySummary.Build` projects existing canonical reviews, training attempts and coaching attempts. `ForgeTodayService.BuildWeekAsync` uses the last seven programme-local dates through its captured `now`; the start is inclusive, next midnight exclusive and future records after `now` excluded. DST offsets are taken at each local boundary. `AsOf` records that capture time. This is JARVIS practice context in the Forge programme's zone, not a claim that all historical records were created by Forge or completed its curriculum.

Owner-only, Forge-gated `GET /api/forge/week` returns404 when off/no programme, without personal runtime reads. The browser Today panel shows the optional summary; failures display a fixed retry message while keeping Today. Off Today never starts a week request. Browser reload/clear generations fence weekly results too; no storage, polling, new voice tool or client authority.

- Reviews use their canonical review time/domain. Known technical domains map to TECH, French/Communication to VOICE and InterviewStory to CAREER.
- Training attempts require the current canonical QuestionBank identity AND version before using its domain. Coaching requires the existing CoachingCatalog identity AND version: Communication maps to VOICE, Interview to CAREER. Unknown identities/versions/domains remain explicitly unclassified; no guessed credit. Total activity includes these records, with separate unclassified counts.
- A canonical attempt with answer history counts once, regardless of corrections/assessments. Never-answered attempts, skipped/deleted training and deleted coaching are excluded. Canonical purge/retention removes credit; there is no separate Forge evidence history to reconcile.
- Focus divisions have a current active tagged goal (created by the capture time) or classified practice. `PracticeCoverage` is the fraction with recorded classified practice, NOT weighted evidence coverage, skill, mastery, outcome or verified evidence. No focus or any unclassified provenance means this fraction is unknown/null. Zero with known focus means no recorded practice, never zero skill. Current goals are current context, not a reconstructed historical plan.
- Actual and planned minutes remain null; no duration or capacity is inferred. Spend, weighted scores/coverage, planned comparisons and next-week proposals remain separate unimplemented requirements.

Aggregation runs inside one existing runtime read callback; only count DTOs leave it. Programme selection and runtime reads remain separately epoch-fenced snapshots, not a globally coherent frozen schedule. The on-demand read still hydrates the canonical cold and learning units; this is not indexed-volume/performance acceptance. No new store/entity/history/schema/engine/provider/action, no protected capacity/key/protector read, no persistence.

The entire combined source needs independent approval before merge. Live Forge remains off; no operational recovery, hardware or release acceptance is implied.

The weekly summary is not yet added to speech; the existing `core_briefing` block and 1,200-byte cap are unchanged. Full evening/weekly spoken review, weighted evidence, budget attribution and actual/planned comparisons remain separate requirements.
