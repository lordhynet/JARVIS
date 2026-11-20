# Android stack for Project Forge

Status: proposed (F0).

The Android companion requirements of 13 Nov 2026 name React Native/TypeScript, while the consolidated specification names Kotlin/Compose. **The repository is Kotlin/Compose** (`JARVIS.MOBILE/app`, version 0.11.3, code 18), with working voice, local wake, earbud routing, recording, reconnect and handoff. Forge extends this client. It is not rewritten, and no React Native code is added.

The Android companion keeps no Forge business logic (ADR 0002, audio-only, Core-owned product):
- Today is spoken through Core's existing briefing; its visual view belongs in the browser. No native dashboard or scoring screen is added.
- Drills use the existing audio transport, audio-route controller and handoff. Core owns turn-based drill state and decisions; using the transport does not require every drill to be realtime.
- The current owner instruction forbids native offline business queues. A future pack/answer-queue proposal must resolve that instruction explicitly before implementation; it is not authorized by this ADR.

While moving, the app plays audio only or stops, and shows no screen prompts.
