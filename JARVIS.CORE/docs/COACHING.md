# Coaching, reviews and programme — Android 0.3.0

Core now serves 24 authored exercises: eight communication exercises and sixteen interview
exercises, including real-story retelling, saved CV-claim defence, interviewer roles,
architecture, incidents, payments and debrief. Android and browser clients share the
same catalog, canonical ConversationSession IDs and transactional PostgreSQL runtime store.
These are structured practice prompts, not an adaptive AI interviewer.

## Answers and evidence

Answers are bounded to 20,000 characters and five saved versions. Save/correct requests
use UUIDs, fingerprints and optimistic revisions. Lists return 50 summaries per session;
the personal test build allows 2,000 records including deletion tombstones. Retry after a
lost response returns the current record, without creating another answer. Changed input
under the same UUID returns 409. Browser tests exercise an actual committed/lost response.

`text-coach-v1` reports word/sentence counts, possible filler phrases, long sentences,
long answers and conditional ownership-language observations with bounded text evidence.
These English-oriented lexical rules are heuristics, not verified correctness, confidence,
personality, a calibrated score or acoustic pause/restart measurements. A phrase such as
"kind of" can have real meaning, so it is labelled a candidate, not an automatic error.

Duration is optional and explicitly **user-reported elapsed seconds** (1–3600), not inferred
or attested audio metadata. Approximate WPM divides word count by that reported elapsed
time, including pauses. Without reported timing, pace/duration remain null. Practice target
seconds are instructions only. No new coaching, programme or report endpoint calls a cloud
provider. Technical progress continues to count technical attempts, not programme check-ins.

## Real sources and deletion

Story exercises require an existing story and current version. CV defence also requires
a valid saved claim index. Prompts resolve source text on demand; attempt metadata stores
IDs/version/index rather than copying claims. Source changes flag old answers as stale
and prevent further corrections until a fresh prompt uses the updated facts.

Deleting a story purges linked coaching answer versions in the same transaction. Deleting
an answer purges all versions. Minimal fingerprints/tombstones reject delayed resurrection.
These operations do not erase separate conversation transcripts, backups or exported files,
and do not claim complete product-wide deletion. Clients still need a reload after remote
changes; universal push invalidation and offline/process-death recovery remain pending.

## Session reviews and programme

Reviews derive current technical/coaching counts, practiced questions, latest known self
ratings per technical question, current-version text observations and next-drill suggestions.
Corrected/deleted historical text contributes no observations. A fingerprint identifies
inputs. Session and runtime reads are separate snapshots, not one serializable cross-store
read; refresh after edits. Browser JSON download is an explicit local export.

The twelve-week programme stores a local start date, time zone and twelve weekly focus
plans. Core computes week 0 before start, 1–12 during the programme, and 13 afterward.
Weekly reflections are explicit self reports, not proof of practice or competence. Future
weeks cannot be checked in early; one reflection is permitted per week. Revision checks
and retries are atomic across hosts. Up to ten programme records including tombstones are
retained. Deletion purges reflection notes while separate practice answers remain. Reminders,
adaptive replanning and automatic completion are not implemented.

## API and clients

All routes use the existing shared Core key; OIDC remains pending.

| Route | Methods / behavior |
|---|---|
| `/api/modules` | GET fifteen module status records with available/remaining work |
| `/api/coaching/exercises` | GET versioned catalog |
| `/api/coaching/prompt` | GET with exerciseId and optional storyId/storyVersion/claimIndex |
| `/api/coaching/attempts` | POST answer and receive text observations |
| `/api/coaching/attempts/{id}` | GET, PATCH correction, DELETE with expectedRevision |
| `/api/coaching/sessions/{id}/attempts` | GET latest 50 summaries |
| `/api/sessions/{id}/review` | GET current derived review |
| `/api/programme` | GET active view, POST dated programme |
| `/api/programme/{id}/check-ins` | POST explicit weekly reflection with expected revision |
| `/api/programme/{id}` | DELETE with expectedRevision |

C# contracts generate Kotlin DTOs. Additive runtime JSON properties require no new EF
schema migration. New browser panels are authored in `../JARVIS.FRONTEND/modules.js`;
`scripts/sync-frontend.ps1 -Check` validates the packaged Core copy. API keys stay in the
original tab's memory. User/provider strings use textContent/value, never interpolated HTML.
Sign-out clears added private forms, histories, process-only drafts and pending IDs.
