# Live voice and proactive agents — audio-only Android 0.10.0

## Setup

The backend runs on .NET 11. Put provider keys in the private root `appsettings.json`: `OpenAI.ApiKey` enables the OpenAI adapters; `Anthropic.ApiKey` enables Claude interview briefs. Model profiles and dated rates live under `AI` and `PaidAI` in that same file. Preserve the existing API key, database password and TLS settings. Apply changes with `scripts/start-server.ps1 -NoBuild`. Provider keys never belong in the Android app.

Connect Android using its saved encrypted connection. The existing ADB HTTPS bridge must remain connected; it is a development route, not an away-from-home deployment. The audio-only companion is `../artifacts/JARVIS-Android-0.10.0.apk`.

## Start a voice session

On Android, verify microphone/earbuds in settings, choose cloud-processing consent and tap Talk. Core prepares, resumes and claims the canonical conversation through `/api/voice/prepare`; there is no native session browser. On the browser, open/create and claim a session before starting voice. Android uses a user-started microphone foreground service with notification and media stop controls. The browser uses an AudioWorklet and streamed PCM playback. Neither a schedule nor an agent starts the microphone.

Core issues a one-use ticket valid for 15 seconds. The WebSocket authenticates with that ticket in its first frame; reusable credentials do not appear in the URL. One expiring, fenced audio lease covers all sessions/devices. Client heartbeats renew it; clients stop playback/capture when renewal expires. Stop, audio focus loss, disconnection and ownership changes stop the live path. The clients can continue in successive bounded 60-second provider windows while the user keeps the session active and budget remains available.

Core controls response generation, context size, input rate and output caps. Speech interruption cancels the response and truncates unheard playback. Completed text transcripts are saved to the canonical session; raw PCM is not archived by this implementation. A provider failure can leave an uncertain charge, so Core retains the conservative reservation rather than assuming it was free.

## AI feedback

Save an answer before requesting AI feedback. The request includes explicit consent and expected record/text versions. The browser displays the model source and evidence quotes; Android has no feedback dashboard. Core rejects fabricated quotes and stale publication; editing an answer clears its current AI-feedback pointer. Personal stories are unverified user claims, so the interview adapter does not assign technical accuracy without a technical reference. Technical assessment uses the supplied question reference and the shared review scheduler.

## Proactive work

Use Proactive agents to create a run or daily schedule. Specialist tasks have claim tokens, leases, deadlines and bounded concurrency. Review waits for the specialist drafts. Global/per-workflow pause controls stop dispatch and late publication; quiet hours delay inbox delivery, and stale notices expire. Research, career and personal planning require cloud consent before creation. Local practice reminders are available without paid processing. Agents produce reviewable drafts and reminders; they do not send messages, change calendars or operate external tools autonomously.

## Current limits

Checks exercise protocol fixtures and canonical state transitions, not live paid-provider quality or phone acoustics. Bluetooth, locked-screen, battery and extended recovery acceptance are pending. Specialist task state lives in the locked PostgreSQL runtime aggregate; dedicated task tables remain a requirement. Nine technical modes, text CV/interview import and retention jobs exist; binary/audio import, broader evaluated content, identity isolation, Windows and Quest/satellite software remain incomplete. Native business/offline replay is excluded by the audio-only Android direction. See `CORE_BACKLOG.md`, `IMPLEMENTATION_STATUS.md` and `VERIFICATION.md`.


## Core-owned voice tools

The realtime provider now receives six fixed functions: `core_context`, `core_practice_current`, `core_practice_start`, `core_practice_answer`, `core_practice_stop`, and `core_recall`. Android remains unchanged audio transport. Core dispatches only completed function calls belonging to responses it requested, while the audio lease and current session ownership remain valid. Cancelled/partial calls are discarded. Four tool calls per window and four calls per response batch are the bounds; malformed/unknown tools return errors and cannot choose a URL or command.

Context includes bounded stories, due prompts and only active goals with their own cloud-processing consent. Reference answers are withheld until an answer is saved. Starting practice uses the existing authored nine-mode drill engine. Current progress and answers are canonical Core training records, shared with the browser and restored after restart. Spoken answers reference a Core-issued ID for captured user speech from the current connection; the tool accepts no model-authored answer text. Retry identity derives from session and utterance, preventing duplicate answers or accidental advancement from a retry. The start instruction itself cannot be submitted as its answer. References are not calibrated scores.

The bridge uses completed response output and returns function results via conversation items before requesting the next bounded response, following the [OpenAI realtime function-calling protocol](https://developers.openai.com/api/docs/guides/realtime-conversations). The provider conversation cap remains 4096 post-instruction tokens; the reservation includes that cap per response and the tool definitions/prefix. See [official truncation reference](https://developers.openai.com/api/reference/resources/realtime/server-events).

Voice changes to goals/stories/CVs, import/deletion, paid assessment and external integration actions remain unavailable. Microphone, provider quality, interruption latency and away-from-home network acceptance require actual configured hardware/provider checks.


## Spoken due-card recall

`core_recall` adds Core-owned due-card review with `start`, `current`, `reveal`, `rate` and `stop` actions. A question is selected from canonical due learning cards; its reference stays hidden until a separate captured user attempt. The current phase persists across provider windows and Core restarts.

After reference reveal, Core accepts only a separate explicit spoken Again, Hard, Good or Easy rating (or a supported simple rating phrase). Ambiguous/negated speech and a model-proposed rating that differs from the user's words are rejected. Rating receipt, schedule change and recall completion are committed atomically through the same scheduling logic as HTTP learning reviews. Repeated/concurrent delivery cannot schedule the card twice. A card changed/reviewed elsewhere invalidates the pending recall; stop and start again to reload it.

Voice review events carry `timingSource: not_measured`; their legacy duration slot is zero and does not establish a measured response time. Question/reference content is bounded for voice, with an explicit incomplete-content flag for long cards. The model must disclose that flag and must not invent the remainder. The recall metadata stores IDs and phase, not another copy of spoken answers. Privacy export includes it; erasure and source/session retention remove it. Android remains the unchanged audio-only transport.

