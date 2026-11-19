# Core enforcement of locked voice sessions

Date: 17 November 2026. Implementation slice: Item 15a, including VR-04. The Windows companion, local greeting and home phrase work remain subsequent slices.

## Decision

Core binds lock metadata to the authenticated device's existing fenced audio lease. `locked` is an optional boolean on voice admission and wake-candidate requests. Missing Windows state means restricted. Existing Android/browser clients retain their prior default when they omit the field. `DeviceKind.WindowsAgent` identifies the clients that must report lock state; changing an enrolled device registration already requires revoking its credential.

Historical desktop browsers also use `WindowsAgent`. Only the existing owner-registered combination of `HasDisplay=true` and the `browser-companion` registration tag is exempted from mandatory lock reports. Untagged Windows registrations and audio-only Windows registrations remain restricted when state is unknown. The exemption comes from Core's registration, never a heartbeat or provider argument.

Every WebSocket heartbeat can report `locked`. A Windows heartbeat with a missing report means restricted. Non-boolean values are rejected. The server derives device kind from its registration, not a client message.

The authoritative lease holds effective `Locked` and the latest nullable `ReportedLocked`. A heartbeat may tighten the effective policy immediately, but it cannot relax it. Only Core's activation of the next window applies an unlock report. Lease renewal ignores client-supplied policy fields on an echoed lease. Existing device/session/fence checks reject expired, replaced or foreign leases before policy changes.

## Context and tools

A restricted window uses generic speaking instructions and empty conversation/practice context. Core does not refresh a private conversation summary or read saved practice context for its prompt. This excludes the current task, earlier conversation turns, stories, CV evidence, learning cards and any personal persona configuration. The model is told that personal information and practice tools are unavailable.

Only the installed home voice tool is advertised in this slice. Core also rejects personal tool calls before parsing their action arguments, so a model cannot gain access by inventing a hidden tool call. Public conversation remains available. Travel tool registration and suppression of route names are part of Item 16; no travel capability is claimed here.

`AllowWhenLocked` is a separate, owner-audited home capability-grant flag. It defaults to false in requests, views, in-memory records and the additive database migration. It participates in grant request identity checks. Locked lookups exclude grants without the flag before resolving names or querying Home Assistant. The flag applies to the entities explicitly listed in that grant. It is accepted only for installed Home Assistant capabilities.

Allowing a grant when locked does not pre-approve its actions. Existing `PreApproved`, entity/action constraints, captured-utterance checks, credential binding, receipts and owner-confirmation rules still apply. Covers, locks and alarms still produce proposals for separate owner confirmation. The execution callback rechecks locked-grant authority as well as current audio ownership.

## A lock during an unrestricted stream

Core cannot make a provider forget personal context already supplied to an active window. When an unrestricted Windows stream reports locked, or omits required lock state, Core sets an in-process forwarding stop, tightens the durable lease and terminates the stream with `device_locked`. Subsequent provider/client forwarding and voice home execution callbacks check that stop. The provider is aborted, the lease is released and unknown usage remains conservatively reserved rather than being refunded without evidence.

A fresh consented admission can start a restricted stream. The future Windows client must return to local listening after this termination and send the current lock state on its next claim. Core does not silently replay a paid call. Unlocking an already restricted stream takes effect at the next actual window activation; prewarming alone cannot expose private context to the standby provider.

The server relies on the enrolled client's OS lock report. This is not remote attestation of the Windows lock screen. No microphone, hardware or acoustic acceptance is established by server fixtures.

## Budget end reasons

VR-04 adds stable reasons `serious_daily_cap` and `gbp_budget_ceiling`. They originate at the existing budget admission checks and survive wrapping in Serious-mode errors. On a terminal budget rejection, the socket retains its error detail and emits an `ended` frame with the specific reason. Declining a negotiated Everyday fallback retains an available underlying budget reason. The reservation, retry and settlement rules are unchanged.

## Compatibility and release

The wire fields are optional, and the Kotlin companion contracts are regenerated. Migration `20261117225515_LockedVoiceHomeGrants` adds one non-null boolean column to `DeviceGrants`, default false, with no modification to existing migrations. Existing grants therefore gain no locked-room authority during upgrade. Existing runtime JSON can be read with the new optional lease fields.

The Windows tray application, installer, real lock-screen checks, across-room wake tests and TfL alerts are outside this commit. Packaging, deployment, APK installation, private-setting changes and paid provider smoke calls require their separate existing authorization.
## Follow-up: locked-room history and admission

Locked voice messages and their runtime projections carry an optional `Restricted` marker, defaulting to false for existing data. Core derives it from the active window and rechecks the canonical lease in the atomic transcript transaction. The marker cannot be changed through a transcript retry. It remains in the dashboard audit transcript but is excluded from recent voice context, derived summaries, text-chat context and captured-speech learning/coaching/domain inputs. Home actions still use the captured utterance under the existing locked grant rules.

The personal-tool gate treats a missing or mismatched lease as restricted. Native Windows devices cannot advance an incoming handoff unless they explicitly report unlocked. Existing JSON storage carries the marker, so no relational model or migration changes are needed. Real lock-to-silence acceptance remains a Windows companion check.
