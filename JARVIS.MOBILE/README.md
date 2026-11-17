# JARVIS audio-only Android companion

Version 0.10.3 supports system-trusted HTTPS for pairing links that name a DNS host. Core owns conversations, memory, practice, CVs, goals, stories and tools. Android handles secure connection, microphone, playback, permissions and start/stop.

Build with JDK 17 and `./build.ps1 -Clean`. This builds, runs JVM tests and lint, and verifies the signed `../artifacts/JARVIS-Android-0.10.3.apk`. Pinned tools are in `../.tools`.

Install through authorized ADB: `adb -s <serial> install -r <apk>`. From Core use `./scripts/connect-android.ps1 -Device <serial>`, then accept Connect to my Core. Provider credentials stay on Core.

Pairing links for a DNS host can use the Android system certificate store. Links for IP addresses and localhost require a certificate fingerprint. A pinned connection is confirmed against its saved certificate and asks you to re-pair if the fingerprint changes.

Physically check microphone and earbuds, confirm the check in settings, grant permissions and consent to provider processing before starting. Core prepares/resumes its canonical conversation. Headset Pause/Stop ends voice; Next interrupts the reply. Start is an explicit screen gesture.

ADB localhost forwarding only lasts while debugging stays connected. Away-from-home use requires persistent private HTTPS/VPN connectivity; installing this APK does not configure that route. Core provider readiness and budget are also required.

Earlier rich-client source is archived outside the build in `legacy-rich-client-0.9-20261114-111954`. Old encrypted drafts remain available for explicit export or confirmed removal, never automatic replay.

Transport recovery uses a fresh Core ticket, bounded delays of 1/2/4/8/8 seconds, and no replay of microphone audio. An unacknowledged old lease drains before retry. Permission, ownership, credential, consent and budget rejections stop recovery. Capture closes between windows, and playback queues at most twenty seconds of PCM. API29/30 Bluetooth SCO is explicitly selected when a Bluetooth output is present. Real network-toggle, screen-off, incoming-call and earbud acceptance remains unverified; automated tests do not establish it.

Core participates in an incoming handoff when you explicitly start voice on the target phone. It marks the target ready, waits for the source to acknowledge stopped audio, and completes the transfer using the existing conversation. If the source does not release, Core refuses the start; the phone does not steal the session.
