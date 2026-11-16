
## Core backlog after audio-only Android

Implemented in the next Core increment: fixed realtime tool dispatch; bounded saved context; authored technical practice start/resume/answer/stop; canonical transcript-backed answers; duplicate/cancelled event handling; current session-owner fencing; persisted due-card voice recall with explicit spoken ratings, atomic scheduling and privacy cleanup. Verification is recorded in VERIFICATION.md.

Remaining work, in order:

1. Extend Core voice orchestration to communication/story coaching, CV/source selection and personal planning. Preserve explicit consent, provenance, request identity and conflict checks. External actions must retain the existing Core action confirmation policy.
2. Persistent private HTTPS/VPN routing for use away from home, then actual provider/earbud/screen-off and interruption/recovery acceptance. Credentials/account setup and hardware acceptance cannot be substituted by fixtures.
3. Adaptive coaching and evaluated scoring, broader technical content, interview sequencing, measured acoustic feedback and evidence-backed reviews.
4. Dedicated specialist persistence, proactive trigger delivery acceptance, identity/user isolation before broader deployment, archive/audio import and retention/restore/soak acceptance.
5. Windows/Quest/room clients where still required. Android stays an audio interface.

Existing text CV/interview import, source-claim provenance, goal tracking, calendar trigger sync, usage reconciliation and retention jobs already exist in Core; earlier gap documents are historical and do not establish that these services are absent. Their remaining format/provider/acceptance gaps should be assessed against the source and tests before adding parallel implementations.


Slices 0/1 central GBP authority are implemented in source following owner approval: see ADR 0003 and VERIFICATION.md. Production activation, actual FX/tax inputs and owner credential setup remain separate operator decisions. No new deployment was performed. Later Domain Registry/Factory, provider routing, device capability, wake and handoff slices remain pending their own bounded implementation work.

## Voice coaching preparation — 14 November 2026

The next product increment exposes the existing Core communication/interview catalogue through `core_coaching_catalog` and validated authored prompts through `core_coaching_prompt`. Saved story context now includes its version and bounded indexed CV claims, allowing existing story/version/claim validation to govern spoken preparation. Realtime instructions distinguish preparation from saved attempts and scoring. Android remains unchanged and audio-only.

This increment prepares exercises only. Persistent voice coaching answer capture, correction, resume/stop lifecycle and calibrated assessment remain unfinished. No deployment, migration, dependency installation or provider activation accompanies this source change.

## Voice coaching lifecycle — subsequent Core increment

Implemented source: canonical transcript-backed communication/story/CV coaching answers, persisted selected exercise, current/resume, stop, idempotent replay, saved-story version fencing, and bounded text feedback. Answers reuse `CoachingService` and its existing history rather than a second store of answer text. The lightweight voice lifecycle metadata uses the existing runtime JSON document; there is no migration or new dependency. Privacy export/erasure/retention cover the new metadata. Android remains unchanged.

Remaining: spoken correction/replacement with explicit revision checks, automatic programme sequencing, voice-driven source/CV selection beyond bounded context, personal-planning actions, and real-provider/earbud acceptance. No source-only test establishes hardware acceptance.

Rollback: pause voice work and preserve a database backup before reverting to an older deployed writer. Do not mix runtime writers that do not know the new `VoiceCoachings` field; older whole-document serialization can discard additive metadata. Canonical coaching answer records retain their existing shape and text endpoints.

## Spoken coaching correction — next increment

Implemented explicit two-turn correction, canonical revision checking, original answer history preservation, idempotent replacement, pending-correction restart recovery and latest completed-feedback resume. The existing text correction operation is shared inside the voice transaction. Stop safely cancels a correction without retrieving deleted answer text. No project, migration or dependency is added; lifecycle fields are additive JSON data.

Still pending: programme sequencing, richer spoken CV/source selection and personal planning, provider/headset hardware acceptance, and deployment review. The previous voice-correction backlog entry is superseded by this increment.

## Independent review remediation increment

Implemented purpose-specific companion credentials and owner step-up for privacy/trust-changing operations; see `claude-review/JARVIS_CLAUDE_REVIEW_REMEDIATION.md` at the workspace root for closed/partial/open findings and migration requirements. Existing phone credentials must be migrated and the legacy key rotated before remote access; the legacy browser credential remains broad. No paid activation or deployment is part of source remediation.
