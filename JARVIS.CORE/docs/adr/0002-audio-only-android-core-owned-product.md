# Audio-only Android, Core-owned product

Accepted by owner direction on 14 November 2026. Supersedes earlier native Android feature requirements.

Android owns secure audio transport, routing, permissions, foreground lifecycle and minimal setup/start/stop controls. Core owns conversation selection/resumption, memory, CVs, stories, goals, practice and tool orchestration. Native business editors, dashboards, scoring and offline business replay are excluded.

Version 0.10 restricts companion REST calls to privacy, voice preparation/readiness/tickets and device operations. Core prepares canonical conversations idempotently and suppresses display transcripts for audio-only devices. Existing Core data and feature APIs remain intact.

Full voice-driven business actions, persistent away connectivity and hardware acceptance remain separate work. Transport tests do not establish those capabilities.
