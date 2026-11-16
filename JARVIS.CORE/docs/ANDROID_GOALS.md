Historical 0.9 implementation: native Android goal screens are superseded and excluded from the 0.10 audio-only build. Core goal APIs/data remain intact. See [architecture decision](adr/0002-audio-only-android-core-owned-product.md).

# Android goals — 0.9.0

Android now uses Core's existing persistent goal API. Goals are available from **Today →
Your goals** or **More → Goals**. Programme, agents and the module inventory remain under
**More → Programme & agents · all modules**; the navigation bar keeps five destinations.

## Using goals

Create a goal with a title, context/next step, workflow, state, next review and deadline.
Date inputs use `yyyy-MM-dd HH:mm` in the draft's displayed time zone. The zone stays fixed
with the draft across restarts/travel. Existing precise timestamps keep their original
instant until edited. New ambiguous/nonexistent clock-change times are rejected.

Local practice reminders never call paid providers. Interview preparation and daily
coaching can use local deterministic reviews without cloud consent. Research, career and
personal planning require explicit cloud consent. Allowed cloud reviews remain subject to
Core configuration, budgets, global/per-workflow pause and quiet hours. Goals never start
the phone microphone. Each goal revision can create one review workflow when due; updating
progress replaces the prior review. Paused/completed/cancelled goals do not start new work.

Update progress through the saved goal's editor. Deletion requires confirmation and the
current revision; Core clears the goal context and stops/clears its linked review tasks.
The activity panel shows Core's bounded recent-run list rather than a full workflow archive.

## Drafts, unknown saves and conflicts

Goal drafts and exact unconfirmed save bodies use the existing encrypted, no-backup
practice snapshot. Edits debounce for 300 ms. Saves persist their ID/body before dispatch.
Goal writes are **not** part of the unpaid offline practice queue: saving/deleting requires
Core, and no goal request is replayed in the background. Drafts can be edited offline.

An unconfirmed request locks its fields and keeps its original goal ID. **Check Core**
compares the current goal against the complete saved payload. Identical create results or
an identical update at exactly the expected next revision confirm the save. **Retry the
same goal save** resends the original body, never a replacement goal ID. Core rejects a
duplicate update with 409; Android then reconciles its acknowledged current version.

Different/newer versions preserve the draft and show Core's current fields for comparison.
Refresh updates the comparison without overwriting the draft. The owner explicitly chooses
to replace the draft or keep it against the latest revision, then reviews before saving.
An absent/deleted goal blocks stale saves; Android never automatically recreates it.

Local drafts can be discarded with confirmation after resolving unconfirmed writes.
An unsuccessful local snapshot write prevents initial dispatch. Existing cache capacity
limits report errors rather than silently evicting pending work. Core's maximum goal list
has a dedicated 3 MB bounded download, including escaped text; other API responses retain
their previous limit. The list renders 25 cards at a time with a Show more action.

## Privacy and acceptance limits

The existing Android privacy generation checks protect these cached fields and pending
bodies. Explicit local sign-out erases them. Fully offline propagation and abandoned cache
identities have the limits described in [privacy controls](PRIVACY.md). A retained local
draft is an owner-controlled copy and is not a complete per-record remote-wipe protocol.

JVM tests cover request identity, consent, dates/DST, exact timestamp preservation,
serialization, reconciliation, conflict refresh and escaped-text bounds. Core HTTP tests
cover create replay, stale edits/deletes, tombstones and paused cloud-consent policy.
These do not establish physical Keystore/process-death, large-font/TalkBack or live paid
workflow acceptance. Live owner goals are not created or deleted to test this increment.

