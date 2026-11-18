# JARVIS Windows voice companion

Item 15b: a .NET 11 Windows tray companion. Core owns conversations, memory, budgets, tools and device arbitration. This app provides local wake recognition, microphone/playback, HTTPS and minimal controls. It stores one CurrentUser DPAPI connection file; it does not save audio or conversation transcripts.

The unsigned MSIX is a **review artifact**. It has not been installed, signed or tested with a real microphone. Do not treat a successful build as acoustic or lock-screen acceptance. The companion needs the Core locked-session boundary, its locked-history follow-up, and the versioned companion protocol in this branch. It refuses older Core versions before claiming audio. The existing live 0.2 deployment therefore needs a separately reviewed update before this companion can connect.

## Setup after review

1. The owner registers a new `WindowsAgent` on Core, with listen/speak enabled, display/home-control disabled, and `extra: ["audio-only"]`. Use a distinct device ID. Confirm its capabilities and issue its own scoped `jdv1_` device credential through Core's owner-authorized device controls. Never use the shared Core key or owner key in this app. Confirming capabilities records an administrative assertion; it does not prove a microphone test passed.
2. The owner/reviewer signs and installs the reviewed MSIX with a certificate matching the manifest publisher `CN=JarvisPersonal`. No signing certificate, trust change or installation is automated here. A plain executable has no package identity and cannot use this recognition API; see the ADR.
3. Open the tray's **Connection and startup** control. Enter `https://zero.tail8d9014.ts.net/` for system certificate trust. For loopback HTTPS, also enter the leaf certificate SHA-256 pin; its name and validity must still match. No HTTP, redirects, user info or keys in URLs are supported.
4. Paste only this Windows device's credential and explicitly consent to live audio reaching Core and its configured AI provider after a wake or push to talk. **Verify and save** contacts Core, verifies the scoped device and current privacy epoch, then stores the connection with CurrentUser DPAPI. Keep credentials out of screenshots and logs. Forgetting the local connection does not revoke it on Core; the owner can revoke the enrolled device there.
5. Install an English Windows on-device speech language (en-GB preferred) and allow microphone access in Windows privacy settings. Choose **Listen locally for Hey JARVIS**. No app HTTP call occurs while waiting for the exact, high-confidence phrase. Setup verification is an explicit network operation.

`Owner:DisplayName` is optional in Core's sanitized example configuration. A configured name arrives in the authenticated enrollment/refresh device response; otherwise the greeting is “Hi.”. Only the arbitration winner greets. Greeting and provider connection run in parallel, microphone upload waits until the local greeting finishes, and Core's first provider window waits for user speech. A post-greeting connection failure gets a short local failure message. Android's matching greeting and the home-phrase parser are separate 15c work.

## Controls and uptime

Right-click the tray icon for setup, local listening, Everyday voice, the current Serious price confirmation, and exit. Left-click turns microphone and audio **off** immediately. Global push-to-talk/interrupt is **Ctrl+Alt+J**; global stop/off is **Ctrl+Alt+Shift+J**. If another program has those shortcuts, use the tray controls. Serious mode needs an explicit current price confirmation and takes effect at the next Core window; it never overrides Core's budget limits.

State text distinguishes Off, Listening locally, Connecting, Live with Core and Unavailable. Local recognition stops before the wake claim and stays stopped during live audio and playback. A phone with audio ownership keeps Windows silent. There is no automatic paid reconnect: Core end, expired ownership or failure closes the socket and stops audio; a new local wake or push to talk must obtain a fresh fenced ticket. Continuous windows renew over the existing socket (300 seconds by default on Core).

Start with Windows is **off by default**. Enabling it explicitly requests the package's Windows startup task and local listening after sign-in; Windows can refuse or disable it. After reboot, sign in once, then lock the screen if desired. Prevent sleep through your power settings. There is no Windows service or session-0 microphone component.

Local wake remains available when the screen is locked. Every claim and heartbeat reports OS lock state; unknown state is restricted. Session lock immediately flushes playback and sends a lock heartbeat. Core terminates an unrestricted stream that becomes locked. A fresh wake can open restricted voice; personal context/tools stay unavailable and home control needs an owner grant with `AllowWhenLocked`. Unlock only relaxes policy at the next actual window renewal. Lock-screen microphone continuity and lock-to-silence latency still need measurement on the owner's installed package.

## Developer verification

Use the SDK in `global.json` on Windows:

```powershell
dotnet build Jarvis.Windows.sln -c Release
dotnet test tests/Jarvis.Windows.Tests/Jarvis.Windows.Tests.csproj -c Release
./scripts/package.ps1
```

The packaging script publishes a self-contained x64 app, generates simple PNG package assets, validates the manifest through Windows SDK MakeAppx and writes `artifacts/Jarvis.Windows_0.1.0.0_x64_unsigned.msix` plus a SHA-256 hash. It never signs, installs, launches or enables startup. Tests use fake audio/speech/socket/provider boundaries and real CurrentUser DPAPI. CI uses a Windows runner and path filters for this folder, shared contracts and its workflow. Application code adds no third-party recognition or audio package; tests reuse the repository's existing xUnit/test SDK versions.

## Owner acceptance still pending

Record actual unlocked/locked wake-to-live latency, greeting-to-listen latency, lock-to-silence time, phone/laptop arbitration, microphone privacy-off recovery, hotkeys, optional startup behavior and output-device behavior after review. Run one hour of TV audio and record the real false-wake count (target at most one); test the exact rejected phrases too. Verify personal-data refusal and permitted home actions while locked against the reviewed Core. No measurements are asserted by the automated suite.
# Governed laptop actions (Item 5, source review pending)

The proposed capability list needs owner approval before installation: `windows.app.open`, `windows.url.open`, `windows.media.control` (play/pause/next), `windows.volume.change` (up/down/mute, 1–10 steps), `windows.workstation.lock`, `windows.notification.show`, and `windows.files.find` (names only). No arbitrary shell, script, arguments or executable path can come from Core or a model. Actions are off by default, independently of local wake listening.

After source/capability approval, the owner must enroll and confirm a native audio-only WindowsAgent, grant only the desired capabilities in Core's Tools tab and fill in local `%LOCALAPPDATA%\Jarvis.Windows\actions.json` using `actions.example.json`. That example is inert and is not copied into the account by a build. App IDs map to exact local `.exe` paths plus SHA-256 hashes; interpreter/shell executables and renamed known shells are refused. URL IDs map to exact HTTPS URLs. Folder IDs map to local plain directories: network paths, traversal, symlinks/junctions/reparse points are refused. Core constraints use `allowedIds` for app/URL/folder IDs, `allowedActions` for media, `allowedActions` plus `maximumSteps` for volume, and `{ "allowed": true }` for lock/notifications. Match local and Core IDs; a Core grant cannot expand the local allow-list.

Enable the explicit laptop-actions checkbox in Connection only when configured. This opts into a separate three-second Core command poll; local voice wake still performs no provider/network call before wake when actions are off. A command is authenticated and bound to this device, argument hash, lifetime and unique dispatch fence. It is journaled with CurrentUser DPAPI before acknowledgement; Core rechecks live grants at acknowledgement before any native call. The protected journal survives restart, refuses reused command IDs and retains unknown outcomes without replay. Pending outcome receipts can be resent without executing again. The bounded journal stops new actions after 2,048 identities until owner maintenance/re-enrollment; it never silently forgets replay protection.

While locked, only media and lock are admitted locally. Spoken locked requests additionally require the Core grant's AllowWhenLocked flag. Voice commands bind to the Core-captured utterance and current audio fence; fence change or loss of audio blocks later delivery/acknowledgement. App/URL/folder aliases are hidden while locked. Configure `WindowsActions:VoiceDeviceId`, `AppAliases`, `UrlAliases` and `FolderAliases` on a reviewed Core; an empty target chooses the active native Windows device or the sole native device, otherwise refuses ambiguity. Examples after configuration: “pause the music”, “open my CV folder” (names only), “lock my laptop”. Reversible actions can use explicit preapproval; file search is read-only and still follows command review when required by the grant policy.

Every received identity has a receipt path; offline outcomes remain in the protected journal. Only Core's non-late verified success receipt permits voice to report success. Receipts distinguish accepted OS requests from unmeasured outcomes: a lock API success means initiation; opening a URL does not inspect page content; volume does not claim a measured final level; notification visibility is not attested. Native media, volume, lock and notification acceptance remain hardware tests, not conclusions from fake-platform tests. Use “Stop laptop actions” to stop polling; microphone/audio Off remains its own control.
