# Interview capture and encrypted audio storage

Date: 19 November 2026. Status: implemented for local review; disabled by default. Track R1 of the owner-approved block-2 plan.

## Consent and data boundaries

JARVIS does not decide whether a recording is lawful; the owner does. Every recording needs both storage consent and this confirmation on the owner dashboard and again on the unlocked capture device:

“I have told the interviewer, or I am otherwise entitled to record this conversation, and it is for my own practice only.”

The interviewer's voice and words are third-party personal data. They are kept only for the owner's own practice and must never enter memory, agent context or domain knowledge. Recordings are never shared, exported to other people or sent to any service other than the configured transcription provider. There is no voice cloning. R1 performs no transcription or provider calls. Subsequent transcription must reuse the existing consent-gated ImportService and budgeted provider, with an explicit confirmed quote; no automatic paid retry.

An elevated owner prepares a ten-minute offer for one enrolled native device and its current credential. That device can poll, start, upload and finish its own offer through the capture endpoint. It cannot create offers, enumerate owner recordings, play, download or delete them. Native consent and an unlocked, visible start are required again. Locking may continue an existing capture; it cannot start one. Owner management requires a manual unlocked client, and deletion uses the existing elevated privacy scope with a revision check. An unlocked flag is the existing client trust boundary, not remote OS attestation.

## Capture and transport

Windows records the default microphone as `user` and the default render endpoint's WASAPI loopback as `interviewer`. Loopback includes **all** playback on that endpoint, not just a chosen meeting application: the owner must silence unrelated audio and confirm the appropriate endpoints. The two sources use a common monotonic timeline, bounded silence padding and discontinuity detection. Endpoint changes or capture failures stop the recording as incomplete. Speaker labels identify the capture tracks; they are not biometric speaker identification, and microphone bleed remains possible.

Android records one microphone track as `unknown`, for face-to-face interviews. It does not record telephone call audio. Calls/audio-focus loss interrupt capture. Start requires a visible unlocked activity, microphone permission, enabled recording notifications and explicit consent. A nonexported microphone foreground service shows “JARVIS is recording” with Stop, and the app shows the same indicator. Windows shows a recording tray indicator and banner. Both clients stop local voice and wake detection before capture and refuse new voice/wake starts while recording. Core independently excludes that device from voice admission for the capture window. Ending a recording releases that exclusion even while protected chunks still await upload. Wake does not automatically restart.

Capture is bounded to three hours and 60-second chunks (including a short final chunk). PCM16, mono, 16 kHz WAV is the implemented format on both platforms. This is a deliberate platform-only fallback rather than claiming AAC compression: it requires no new dependency and provides exact sample counts and bounded chunks of 1,920,044 bytes, below the existing transcription upload limit. Transport includes track, sequence, SHA-256 and a stable recording ID. Replays acknowledge matching audio without another logical write; changed audio at an existing sequence is rejected. Finish declares immutable per-track counts. Missing/noncontiguous chunks, short interior chunks, mismatched track lengths or any declared capture discontinuity remain `Incomplete`. Later unpaid upload recovery can fill missing chunks; it never restarts the microphone. Native buffers are zeroed where owned; JSON/base64 and framework-managed buffers cannot promise immediate heap erasure.

Native queues encrypt chunks and their manifest before disk: Windows uses current-user DPAPI; Android uses a separate AES-GCM Android Keystore key. Legacy draft keys/data are preserved. Queue identity binds the Core origin, enrolled device, privacy epoch and recording ID. Only an exact Core acknowledgement removes a queued chunk. Ordinary network/authorization failure retains protected audio, with bounded upload batches/backoff. Re-pairing cannot silently replay a previous credential's job. Confirmed erasure/epoch change removes queues for that Core origin; other Core identities remain separate. A process restart marks an interrupted capture incomplete and offers upload recovery only. Unuploaded capture tail can be lost on process death; the app does not claim complete recovery.

## Core storage and privacy

Recording/chunk metadata has dedicated `InterviewRecordings` and `RecordingChunks` tables, a unique recording/track/sequence index and cascading chunk deletion. Recording collections are absent from runtime JSON. Only a bounded recording exclusion ID/expiry is added to the registered device in the hot audio row. Metadata, exclusion updates and privacy checks use the existing PostgreSQL privacy gate and D2 transaction. In-memory mode uses the corresponding shared privacy boundary.

Audio files use AES-256-GCM with random nonces and authenticated epoch/recording/track/sequence/hash/sample/blob metadata. Files contain ciphertext only, under code-owned epoch/GUID paths; traversal, symlinks and reparse points are refused. A random physical blob ID makes uncertain database-commit retries independent. An uncertain commit retains the encrypted blob instead of risking deletion of acknowledged audio. These crash orphans count against the global 2 GiB quota and are swept by recording deletion, retention or erasure. The database and filesystem are not a single atomic resource.

Audio retention is 30 days from start. An independent hourly worker removes audio even if general artifact retention is disabled; transcripts/metadata may remain under their existing retention rules. The worker also closes expired/revoked captures. Explicit deletion removes recording audio/metadata and any linked imported transcript through ImportService's purge path. Erasure removes all owned recording blobs, including crash orphans, under the exclusive privacy boundary before it reports success. Deletion failures fail closed. File deletion precedes the database commit: a failed commit can therefore leave metadata whose audio is unavailable; retry removal rather than pretending the bytes remain. Core data export contains recording metadata only, with no audio, paths, credential hashes or encryption keys. R1 has no audio export endpoint.

## Configuration, backup and rollback

`Recordings:Enabled` defaults to false. Set `Recordings:Directory` and a **stable secret 32-byte base64 AES key** in `Recordings:EncryptionKey` through the deployment's private configuration. The example has placeholders only. Do not use Core's ephemeral data-protection provider for persistent audio. Changing/loss of this key makes existing ciphertext unreadable. The compose source adds a dedicated `/app/recordings` volume and the image source creates an app-owned directory; neither image nor deployment was built or changed during this work.

Back up the database, recording volume and key together at a consistent point, with protected key backup. A database-only restore cannot recover audio; a ciphertext-only backup is not a usable recording catalog. Migration downgrade removes the tables and device recording exclusions; it deliberately does not delete the recording volume. Export/restore the supported database and encrypted volume before rollback, preserve the key, and stop active captures before changing versions. Reapplying a downgraded migration creates empty recording tables; it does not reconstruct discarded metadata from blobs.

Physical acceptance remains necessary: real laptop microphone and meeting loopback separation, default-device changes, microphone bleed, long-run clock alignment, suspend/lock, interruption, network loss/recovery, S22 lock/foreground/notification behavior and battery impact. Automated source, fake-transport, encryption and migration tests do not establish hardware quality.

## Platform references

- [Microsoft: WASAPI loopback recording](https://learn.microsoft.com/en-us/windows/win32/coreaudio/loopback-recording)
- [Microsoft: IAudioClient initialization](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudioclient-initialize)
- [Microsoft: capture buffer flags and timestamps](https://learn.microsoft.com/en-us/windows/win32/api/audioclient/nf-audioclient-iaudiocaptureclient-getbuffer)
- [Android: foreground service types](https://developer.android.com/about/versions/14/changes/fgs-types-required)
- [Android: restrictions on foreground service starts](https://developer.android.com/develop/background-work/services/fgs/restrictions-bg-start)
