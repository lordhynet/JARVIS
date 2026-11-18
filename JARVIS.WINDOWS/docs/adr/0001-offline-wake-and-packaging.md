# ADR 0001: Windows offline wake and package identity

Date: 18 November 2026. Status: implemented for source review; hardware acceptance pending.

Use Windows `SpeechRecognizer` with one `SpeechRecognitionListConstraint`, “Hey JARVIS”, and accept only High confidence. English grammar resources are required locally. Reject other phrases rather than falling back to dictation or a cloud/topic recognizer. Recover a successfully completed or silence-timed-out continuous local session only while listening remains enabled; resource/privacy failures show Unavailable. Wake recognition is stopped before claiming live audio and throughout playback. Platform boundaries are behind interfaces, allowing tests without microphone access.

Microsoft documents that list/SRGS grammars run on-device and that these APIs require package identity. Therefore choose an unsigned, self-contained x64 **MSIX review artifact**, rather than the plan's suggested plain zip. This is a packaging correction required by the chosen framework API, not a new recognition dependency. Package installation/signing/trust decisions remain with the reviewer and owner. An unpackaged binary reports a clear unavailable state rather than silently selecting a different recognizer.

Use WinRT AudioGraph at 24 kHz mono 32-bit float internally and convert explicitly to/from the protocol's little-endian PCM16. Capture uses synchronous `QuantumStarted` cadence. Playback and capture queues are bounded; hardware callback failures cancel audio ownership. Local speech synthesis stays in memory. No audio files are created. Startup uses a package StartupTask with `Enabled=false`; enablement is requested only through the owner's checkbox and Windows may refuse.

Core owns wake arbitration, one-use tickets, fenced audio, privacy epochs, budgets and 300-second windows. The first protocol version advertises lock support; Windows refuses an older server before claiming. Only a winner synthesizes a local greeting using Core's bounded owner display name, then opens upstream capture after it finishes. The provider is instructed to wait for user speech. OS lock reports are conservative: access failure or a non-default input desktop counts as locked; this is client reporting, not remote attestation.

Option B (openWakeWord/ONNX) and NAudio remain unselected. Any new recognition/audio package needs reviewer approval. Do not switch based on assumed recognition quality: first measure across-room wake latency and one hour of TV false wakes on the installed package. Those values, lock-screen behavior, audio routing and echo/barge-in acceptance are pending, not inferred from unit tests.

Primary references checked 18 November 2026:

- [Microsoft: custom recognition constraints and package identity](https://learn.microsoft.com/en-us/windows/apps/develop/input/define-custom-recognition-constraints)
- [Microsoft: on-device grammar language resources](https://learn.microsoft.com/en-us/windows/apps/develop/input/specify-the-speech-recognizer-language)
- [Microsoft: AudioGraph formats, buffers and synchronous processing](https://learn.microsoft.com/en-us/windows/apps/develop/media-authoring-processing/audio-graphs)
- [Microsoft: continuous recognition lifecycle](https://learn.microsoft.com/en-us/uwp/api/windows.media.speechrecognition.speechcontinuousrecognitionsession)
- [Microsoft: desktop packaging extensions and startup tasks](https://learn.microsoft.com/en-us/windows/apps/desktop/modernize/desktop-to-uwp-extensions)
