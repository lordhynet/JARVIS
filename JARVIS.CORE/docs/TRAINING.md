> Android 0.6.0 adds nine persisted guided-drill workflows, saved scenario/code context, sequential stages and cross-device resume. See [guided drills](GUIDED_DRILLS.md) for current coverage, API and limits. The older three-mode path below remains compatible.
# Technical practice — Android 0.2.0

This increment implements a text-first subset of master requirements sections 6–7:
16 authored questions across eight technical domains, answer histories, explicit self
assessments, corrections, deletion and shared spaced repetition. Rapid Drill, Explain
Simply and Recall Review provide different practice instructions. The remaining drill
modes and automatic coaching are pending. No provider call is made by these endpoints.

## One canonical history

Every attempt references an existing Core ConversationSession. Android creates a normal
session if necessary, then checkpoints `training-question:<questionId>`. It resumes that
session and its saved attempts; it does not create another training-session store.
Answers may be recorded against an ended session as retrospective practice; this never
reactivates audio or takes device ownership. Drafts are retained only in process memory
and cleared at sign-out. Unsaved process-death/offline recovery remains pending.

Canonical attempts, immutable answer/assessment versions and operation fingerprints
live in the existing transactional runtime JSON document. PostgreSQL row locking makes
cross-host retries and revision checks atomic. This remains a personal-scale store:
10,000 total attempt records including deletion tombstones, five answer versions and
five assessment versions per attempt, 20,000 characters per answer. Lists return summaries
(50 per session, 10 dashboard recent entries); individual reads contain bounded history.
There is no automatic history export or unlimited archive in this increment.

## Honest assessment

Scores are optional integers 0–4 with an explicit dimension, rationale and answer evidence.
Strong scores (3–4) require an exact excerpt from the current saved answer. Evidence is
checked against that answer. Source is always `self`, rubric `self-rubric-v1`; these are
user opinions, not verified technical correctness. Android currently captures technical
accuracy; the API accepts seven content/structure dimensions. Missing scores remain null.
Communication structure alone cannot establish content mastery. Typed text has no measured
duration or WPM; both metrics remain null.

Saving a correction appends an answer version and clears the canonical assessment.
Historical assessments remain inspectable but no longer affect mastery. A stale revision
returns 409. UUID request IDs deduplicate submissions, assessments and corrections; a
different payload under the same ID returns 409. A replay returns the current canonical
record, which may have advanced since the original request. Android retains pending IDs
for network retries during the current process. It does not silently retry paid requests.

Deleting an attempt clears all saved answer and assessment text, hides it from reads and
recalculates its question's schedule. Minimal IDs/fingerprints remain to reject resurrection
by delayed retries. Existing database backups follow the separate backup retention policy;
this endpoint does not rewrite backups or claim product-wide deletion completion.

## Shared review schedule

Training questions use the existing LearningItem identity, due queue and manual review
history. Content mastery is the average of available content dimension scores, excluding
communication structure. `content-score-v1` schedules low (below 2) after one local calendar
day, partial (2 to below 3) after three, strong (3–4) after seven, and successive strong
attempts after fourteen. Unknown scores and skips contribute no failure or mastery event.
An unseen skipped question does not create a due card.

Scheduling anchors to the original practice timestamp, not the time of later feedback.
Core records the assessment's time zone (Android currently Europe/London). It preserves
local wall-clock time over DST; an invalid time advances one hour to the next valid time,
and an ambiguous time uses its first occurrence. Tests cover both DST boundaries.

Core retains a pre-training card baseline and the IDs of manual reviews already included
in it. Later manual ratings and current assessments replay by practice/review timestamp
and shared sequence. Correcting/deleting earlier feedback therefore recomputes later
streaks without counting old reviews twice or removing later manual reviews. Existing
manual ratings keep their `manual-interval-v1` intervals. Duplicate manual review responses
remain their original historical result; a fresh LearningItem read is canonical.

Dashboard practice days count submitted non-skipped answers grouped by the requested time
zone, never app opens. Weak-domain averages use the latest known score per question and
are labelled self-rated. They are not a calibrated estimate of professional competence.

## API

All routes require the existing Core API key.

| Method | Route | Purpose |
|---|---|---|
| GET | `/api/training/questions` | Versioned authored catalog with primary-source links |
| GET | `/api/training/learning` | At most 16 canonical learning cards for catalog questions |
| POST | `/api/training/attempts` | Save answer or explicit skip |
| GET | `/api/training/attempts/{id}` | Canonical record and saved versions |
| GET | `/api/training/sessions/{id}/attempts` | Latest 50 summaries for a session |
| POST | `/api/training/attempts/{id}/assessments` | Add self assessment with expected record/text revisions |
| PATCH | `/api/training/attempts/{id}/answer` | Append corrected answer with expected revision |
| DELETE | `/api/training/attempts/{id}?expectedRevision=N` | Delete personal text and recompute schedule |
| GET | `/api/training/dashboard?timeZone=Europe%2FLondon` | Counts, self-rated domains and recent summaries |

Canonical C# DTOs are in `Jarvis.Contracts/Training.cs`; Kotlin DTOs are generated from
them. No EF schema migration is needed for additive runtime-document properties.
