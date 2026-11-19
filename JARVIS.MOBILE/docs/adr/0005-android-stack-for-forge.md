# Android stack for Project Forge

Status: proposed (F0).

The Android companion requirements of 13 Nov 2026 name React Native/TypeScript, while the consolidated specification names Kotlin/Compose. **The repository is Kotlin/Compose** (`JARVIS.MOBILE/app`, version 0.11.3, code 18), with working voice, local wake, earbud routing, recording, reconnect and handoff. Forge extends this client. It is not rewritten, and no React Native code is added.

The Android companion keeps no Forge business logic (ADR 0002, audio-only, Core-owned product):
- The "Today" card reads Core's read-only snapshot.
- Drills use the existing live-voice service, audio-route controller and handoff.
- Offline packs (F9) are an encrypted, bounded cache plus an answer queue that syncs idempotently to Core.

While moving, the app plays audio only or stops, and shows no screen prompts.
