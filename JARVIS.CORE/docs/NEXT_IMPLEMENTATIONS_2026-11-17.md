# Next implementation sequence

Written 17 November 2026 from the reviewer's `claude-review/CODEX_NEXT_5_2026-11-17.md` updates at 20:45 and 21:08 and the A2(f) specification in `JARVIS_STATUS_REVIEW_AND_PLAN_2026-11-17.md`. These are implementation slices, not completed features. The reviewer specification takes precedence if it changes.

## Current handoff

A2(f) modernises the existing HTTP Anthropic adapter. Its completion evidence belongs in `CODEX_ANTHROPIC_A2F_2026-11-17.md`. Coach/deep-task routing, reviewed real prices and private settings remain owner configured. The wider model-routing research remains on hold. No deployment or GitHub push is included.

The reviewer recorded Release 0.2 deployed at 21:08 as source `505743d`, image `jarvis-api:0.2.0-850d190`. This work branches from that release plus `review/voice-followups` and targets the subsequent candidate. That recorded deployment does not authorize deploying these changes.

Item 15 follows A2(f). Item 16 is independent product work but shares voice context and locked-mode policy, so its integration follows those interfaces. Keep each logical slice in a separate commit after the required full checks. Stop at each reviewer's ★ checkpoint, with a report and actual verification evidence.

## 15a — Core locked-session boundary

Start in `src/Jarvis.Contracts/Intelligence.cs`, `Endpoints/VoiceEndpoints.cs`, `WakeCandidateArbiter`, `CompanionVoiceService`, `Voice/RealtimeGateway.cs`, the voice context builder and `Voice/CoreVoiceTools.cs`.

1. Add compatible lock-state metadata to wake admission and window renewal. Bind it to the authenticated device and current fenced audio lease. Require a reported state for Windows admission; unknown Windows state uses restricted context. Existing clients retain their supported protocol.
2. Have Core build a restricted context while locked, excluding stories, CVs, learning cards, health and calendar. Enforce the same policy in each personal-data tool handler. Prompt wording alone cannot grant access.
3. Add an additive `AllowWhenLocked` grant setting. Only explicitly granted home entities with this setting may act while locked. Locks, covers and alarms retain their existing owner-approval requirement. Never infer a grant from a model argument.
4. Apply reported unlock at the next fenced window renewal. Ensure a stale ticket or previous lease cannot change another device's session restrictions.
5. Keep travel status available as public information, with route names suppressed when locked.
6. Carry VR-04 into this slice: return a specific end reason when an overlapping renewal reservation reaches the Serious daily cap or development budget ceiling. Preserve the accounting and refusal boundary.

Acceptance fixtures cover locked context exclusion, direct personal-tool refusal, allowed lights, refusal for unmarked entities, dangerous-action approval, renewal transitions and stale leases. Regenerate Kotlin contracts and run Android tests if shared wire DTOs change.

## 15b — Windows thin companion

Create `JARVIS.WINDOWS` with a .NET 11 tray app referencing `Jarvis.Contracts`. Core owns conversation state and business rules. Keep platform audio/recognition behind interfaces so lifecycle and networking can be tested without a microphone.

1. Enrol as a separate `audio-only` device. Store its `jdv1_` credential using CurrentUser DPAPI or Windows Credential Manager. Support the owner-configured trusted HTTPS address, including system trust for the Tailscale hostname and a certificate pin for loopback HTTPS.
2. Use Windows offline `SpeechRecognizer` list constraints for exactly **Hey JARVIS**, with a confidence threshold and a clear unavailable-resource state. Reject “hello Jarvis”, “Jarvis” and “hey Travis”. Local listening makes no network call before a wake. Do not acquire ONNX Runtime or another recognition package without the reviewer's approval.
3. Reuse wake-candidate arbitration before greeting or taking audio ownership. Use the existing one-time ticket, WebSocket, fenced lease and 300-second renewal protocol, including Everyday/Serious transitions. A phone holding the lease keeps the laptop silent.
4. Capture/play PCM16 mono at 24 kHz using WinRT audio APIs. Stop wake recognition during playback. Reuse the protocol's interrupt path for barge-in. Do not store audio on disk.
5. Show off, local listening and live-with-Core states in the tray. Provide global push-to-talk/stop hotkeys and a one-click off control. Start with Windows is optional and off by default.
6. Continue local wake detection at the lock screen, reporting lock state on admission and every renewal. Pause for microphone privacy being off, conflicting audio ownership or owner off. Document that the owner signs in once after reboot and prevents sleep. A session-0 Windows service is outside this slice.

Tests cover wake matching, zero pre-wake HTTP calls, phone/laptop arbitration, state transitions, encrypted credential persistence, contract/route reuse and audio shutdown on failure. Add a Windows-runner CI job with path filters for `JARVIS.WINDOWS/**`, contracts and its own workflow. Editing the workflow does not authorize a push.

## 15c — Winner greeting and home speech

1. Add `Owner:DisplayName` to the sanitized settings and the authenticated enrolment/refresh response. Both Windows and Android use the configured name for a short offline greeting.
2. Only the arbitration winner greets. Start the local greeting while ticket/provider setup progresses, hold upstream microphone frames until it ends, and use first-window instructions that wait for user speech rather than greeting again. A post-greeting connection failure plays a short local failure message.
3. Never put the greeting into the conversation transcript. Trailing wake-audio buffering remains deferred by the owner's selected flow.
4. Extend `Voice/HomeVoiceRequest.cs` to parse clauses separated by punctuation or “and then”. Require exactly one clause starting with the command verb, with no command verbs in the other clauses. Preserve negation rejection and Core-captured-utterance verification. Reject compound home commands and model-only requests.
   Include the deferred VR-03(c) phrase-normalisation follow-up from the release review.
5. Add `Home:Rooms:{deviceId}:DefaultLight`. Resolve “the light” only to exactly one configured, granted room light; otherwise ask which light.
6. Add exact owner-configured arrival phrase mappings in `Home:VoicePhrases` to one granted Home Assistant scene. A phrase with no other command may activate its mapped scene. Require `AllowWhenLocked` for the mapped scene and reuse command receipts. Do not add a routine engine.

Fixtures cover silent losers, configured names, greeting/audio gating, failure audio, transcript exclusion, negation, multiple commands, missing default lights, ungranted scenes and receipts.

**Item 15 ★ review packet:** provide the offline-recognition ADR, a plain zip or unsigned self-built MSIX packaging decision, build/test results and any remaining owner checks. Record real greeting/conversation/light-action latency, one hour of TV false-wake results, cross-device arbitration and locked-room behaviour only after actually measuring them. Do not invent measurements or install before review.

## 16a — Saved travel routes

Use additive persistence in `src/Jarvis.Persistence/SessionDbContext.cs` and a dedicated travel store. Add `TravelRoutes` rather than expanding the runtime JSON document.

1. Store route ID/name, validated TfL line IDs, optional NaPTAN stop IDs, local route windows, Europe/London timezone and enabled state. Limit the owner to 10 routes and each route to 12 lines.
2. Add owner-only list/create/update/delete endpoints, using the existing Owner policy. Add a “My routes” browser card with a line picker from the daily-cached TfL modes catalogue.
3. Update `Integrations/ProviderGateway.cs` and briefing composition to use saved routes. Retain default lines only when no route exists.
4. Keep the TfL request to public line/stop IDs. Redact the entire query when logging URLs so `app_key` cannot appear.

Tests cover ownership, unknown IDs, limits, window validation, GMT/BST transitions, daily catalogue caching, briefing fallback and log redaction. Preserve existing migrations and add a new one.

## 16b — Public travel voice tool

Add `core_travel` to the existing tool registry, with read-only `status` and `arrivals` actions. Status accepts saved routes or a named line. Arrivals uses a saved stop and returns the next five departures.

Use bounded typed results and instructions for natural spoken answers. Do not require home/device action grants. Allow public status while locked, suppress saved route names, and send only TfL IDs upstream. Include at most three active public alerts totalling at most 300 UTF-8 bytes in the next voice window's context. Never inject an alert into an utterance already in progress.

Fixtures cover read-only dispatch, locked privacy, saved-stop bounds, source failures, alert byte limits and next-window context delivery.

## 16c — Debounced travel monitor

1. Poll one batched status request every two minutes for enabled routes active now or starting within 60 minutes. Make zero calls outside that eligibility period. Apply jittered error backoff at 2, 4 and 8 minutes, capped at 15.
2. Add `TravelLineStates` with confirmed severity/reason hash and notification timestamps. Persist candidate transition state as needed to require the same new state on two consecutive polls, including across restarts.
3. Notify for good service becoming disrupted, worsening severity, materially changed disruption reason and recovery. Limit each line to one notice per 15 minutes, with at most six daily disruption notices plus recovery notices, as specified by the reviewer.
4. Send a window-start heads-up for an already-disrupted line. Deliver proactive notifications only inside opted-in route windows; look-ahead polling does not authorize an early notification. Quiet-hours bypass applies only inside those windows. Alarm-linked advance checks wait for Phase C alarms.

Use fake clocks and fake TfL responses for no-poll periods, local window boundaries, transient blips, consecutive confirmation, worsening/reason/recovery changes, notification limits, restart persistence and quiet-hours rules. Do not treat the numeric severity code as an ordering until its official meaning has been checked.

## 16d — Existing outbox and Web Push

Extend the existing outbox and `Notifications/PushNotifications.cs` with category `Travel`. Only this category has a public-safe push summary. Keep the generic private-inbox body for every other category. `Travel:NotifyWhenAgentsPaused` defaults to true and allows travel delivery through agent pause only under that setting.

Add the owner-only simulation endpoint, disabled unless `Travel:AllowSimulation=true` and always refused in Production. Reuse the existing encrypted Web Push path rather than adding native push infrastructure or Android feature screens.

Owner setup in the report must include a free `TfL:AppKey`, the existing VAPID settings, and allowing notifications in Chrome on the S24/S22 at the trusted Tailscale address. Private configuration is filled by the owner/reviewer. Tests cover category privacy, agent pause, simulation authorization/environment refusal and outbox idempotency.

**Item 16 ★ review packet:** include migration rehearsal and automated results. Report measured simulated-disruption-to-phone notification time and recovery delivery only after owner device verification. Submit for review before release.
