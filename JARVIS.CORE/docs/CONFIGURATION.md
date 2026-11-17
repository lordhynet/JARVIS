# Connections and devices

Use the private root `appsettings.json` for all backend deployment settings. It is Git-ignored and excluded from Docker images; containers mount it read-only. `appsettings.example.json` is a sanitized template for new installations and isolated tests. Production does not load user-secrets, environment overrides, command-line settings or environment-specific JSON. Never commit real keys or tokens.

| Setting | Purpose |
|---|---|
| `Jarvis:ApiKey` | Shared operator credential used by the companion and API clients |
| `Jarvis:StorageProvider` | `Postgres` or `InMemory`; default is `InMemory` |
| `Database` | Host, Port, ExposedPort, Name, Username and Password; Core constructs its PostgreSQL connection from these fields |
| `OpenAI:ApiKey` | OpenAI API credential, kept on the server |
| `OpenAI:Model` | A Responses-compatible text model available to your account; choose explicitly |
| `OpenAI:TranscriptionModel` | File-transcription model available to your account; current documented starting model is `gpt-transcribe` |
| `HomeAssistant:BaseUrl` | Your Home Assistant instance, e.g. `http://homeassistant.local:8123` |
| `HomeAssistant:Token` | Long-lived access token for that instance |
| `HomeAssistant:AllowedEntities:0` | First allowed entity, e.g. `light.desk`; add numbered entries for others |
| `Calendar:Google:AccessToken` | Current Google OAuth token with calendar event read access |
| `Calendar:Google:CalendarId` | Optional calendar identifier; defaults to `primary` |
| `Calendar:Microsoft:AccessToken` | Current delegated Microsoft Graph token with calendar read access |
| `TfL:AppKey` | Optional TfL developer key |

Tokens must be obtained through each provider's own authorization flow. This version does
not include an OAuth login/callback UI, token refresh, or calendar writes. Expired tokens
produce a source error; the briefing still shows healthy sources. Do not use a ChatGPT
login token as an OpenAI API key.

`scripts/start-server.ps1` derives temporary Compose substitutions from that same JSON file and restores the process environment afterwards. Model names and dated rates are configured in the `AI` profiles. Cloud requests are bounded
to 20 per minute per API process. Input audio is limited to 10 MB and browser recordings
to one minute. Chat uses the last 20 transcript messages and the last three checkpoints
as context, with a 1,500-token output limit. No model tool calling is enabled: home actions
must use the explicit prepare/confirm workflow.

## Browser and device setup

1. Open the companion on localhost, or on a trusted HTTPS endpoint from another device.
2. Connect with the shared JARVIS API key. It stays in tab memory, not local/session storage.
3. In **Devices**, register a meaningful name. This stores a browser identifier locally.
4. Click **Test microphone & speaker**. Grant microphone access, listen to the speaker
   test, and click the confirmation only if it worked. Verification records your assertion;
   it is not hardware attestation or a measured acoustic test.
5. Open a conversation and click **Use this device**. It can now record or speak.

The browser sends a heartbeat every 15 seconds; devices are considered offline after
45 seconds. Re-registering resets capability verification. The shared-key design trusts
all authorized clients: device IDs are not independent security identities. Use this on
your own trusted devices, not as a multi-user public service.

## Move a conversation

Open the same session on both devices. On the source, select an online verified target
and choose **Move conversation**. On the target, choose **Ready on this device**. The source
stops microphone and playback before acknowledging release. Then the target can choose
**Take over here**. The transfer must finish within 30 seconds. If it expires after release,
the session remains unowned until a verified online device claims it. If the source has
not released, it retains ownership; pausing and resuming the session provides explicit
operator recovery from an abandoned device.

Audio ownership is coordinated through the server protocol and cooperative clients.
The browser polls every three seconds and stops audio on polling failure, ownership
change, page hiding or explicit stop. Native clients must implement the same stop and
handoff rules; this repository does not include satellite firmware or native mobile apps.

## Voice behavior

**Record voice** starts microphone capture. **Stop & transcribe** sends the recording to
OpenAI and places the returned text in the composer. Review it before sending. **Stop
audio** discards an active recording and stops speech/recognition. **Read replies aloud**
uses a browser-generated voice, disclosed in the interface as AI-generated.

**Enable “Jarvis”** uses browser speech recognition while the page is visible. Browser
support varies, and the browser may send audio to its own recognition provider. It is
not an offline wake-word detector and does not run reliably in a locked/background phone.
It fills the composer after hearing the wake word; it never sends a message automatically.

## Home actions

Only `turn_on`/`turn_off` on configured `light.*` and `switch.*` entities are supported.
Prepare an action, review the exact entity and action, then confirm. Confirmation expires
after five minutes. Retrying a confirmation never sends the action twice. Requests marked
as originating from external content are rejected. A dropped provider response or abandoned
running request becomes **Uncertain**; inspect the physical device before making a new request.

### Device capability grants

Use the owner-authorized `POST /api/devices/{id}/grants` for a device with a current credential. Include a new UUID in `requestId`, a reason in `reason`, the installed `capabilityId` and `capabilityVersion`, and a future ISO 8601 `expiresAt` within 365 days. The descriptor from `GET /api/device-capabilities` supplies the constraint schema. For `homeassistant.turn` version `1.0.0`, set `constraintsJson` to a JSON string containing an object such as `{"allowedEntityIds":["light.kitchen","switch.porch"]}`. Each grant can name 1 to 50 distinct entities.

Core records the verified owner subject, reason and request ID. An identical retry returns the existing grant. Use a new request ID to approve different constraints or a newly rotated credential. The owner grant list reports `effective` using the current credential, the stored constraints, expiry and revocation. Grants created before audit metadata was required remain visible with `effective: false`.

Revoked audited grants stay eligible for lookup by request ID through their original expiry to keep retries from reactivating them. Expired grants are eligible for retention pruning. Core erasure removes grant records and their audit data.

### Device command proposals

- `POST /api/devices/{id}/commands` takes a caller-generated UUID in `requestId`, the approved `grantId`, `argumentsJson` as a JSON string, and a UTC or offset-aware `expiresAt`. Choose an expiry within five minutes and within the grant's lifetime. Core returns the existing record when the same request ID is retried with the same device, grant, exact argument text, expiry and invocation origin. Changing any of those fields returns HTTP 409.
- `GET /api/device-commands/{id}` returns the current command and its exact argument text. The proposal and lookup routes use the ordinary Core owner session.
- `POST /api/device-commands/{id}/confirm` requires the separate owner key and an explicit `approve` value. Send the returned `argumentHash`, which is the uppercase SHA-256 of the UTF-8 argument text. Core rechecks the grant, credential and allowed arguments before accepting approval. Approval moves an awaiting command to `Pending`. A negative decision cancels an awaiting or pending command. The response retains the verified owner's decision timestamps and subject.

Agent, voice and external-content proposals require manual owner confirmation. The installed Home Assistant descriptor also requires confirmation for manual proposals. Expired commands are eligible for removal under the configured artifact retention period. Core erasure removes all command records.

### Device command delivery

An executor polls `POST /api/device-commands/claim` with its paired credential in `X-Jarvis-Key` and a new UUID in `requestId`. Core takes the device identity from the credential, checks its current grant and confirmation, and atomically dispatches the oldest eligible command. An HTTP 200 response includes the command and dispatch request IDs, capability version, exact argument text and hash, dispatch time and expiry. HTTP 204 means the poll produced no command.

Reuse the same poll request ID after a lost response. While the command and grant are active, a retry can recover the original dispatch for capabilities marked `CallerKey` or `SafeReplay`. A `NeverReplay` capability returns HTTP 204 on retry. An executor must follow the descriptor's replay policy, use the returned command ID as the action idempotency key when required, and check the expiry before executing. Use a fresh request ID for the next poll after a received response.

### Signed device receipts

The paired executor posts to `POST /api/device-commands/{id}/receipts` with the credential that claimed the command. The body contains a new UUID in `requestId`, the successful poll's UUID in `dispatchRequestId`, a `kind` of `Acknowledged`, `Succeeded` or `Failed`, `payloadJson` as a JSON string that matches the installed receipt schema, and `signature` as 64 hex characters.

Sign with HMAC-SHA256 using the UTF-8 paired credential as the key. The message is UTF-8 with six LF-separated lines and no trailing newline. Write `jarvis-device-receipt-v1`, the command ID, the dispatch request ID, the receipt request ID, the case-sensitive kind above, and the uppercase SHA-256 of the exact UTF-8 payload string. Format UUIDs in lowercase with hyphens.

Core accepts one acknowledgement and one terminal receipt per command. A terminal receipt can arrive directly after dispatch. Retrying the same receipt ID and payload returns the original receipt. A receipt received after expiry is marked `late` and cannot change the expired outcome. The owner can list receipts with `GET /api/device-commands/{id}/receipts`. Command retention and erasure remove their receipts.

### Home Assistant command adapter

To enable the adapter inside Core, register a dedicated device, issue its paired credential and grant it `homeassistant.turn` version `1.0.0` for specific lights or switches. Configure `HomeAssistant:ExecutorDeviceId` and the secret `HomeAssistant:ExecutorCredential` in the owner's deployment configuration. The entity also needs to be in `HomeAssistant:AllowedEntities`, and Core uses the configured Home Assistant URL and token.

The adapter polls for that device's approved commands. The existing `/api/tools/home`, `/api/tools/{id}/confirm` and `/api/tools` routes keep their confirmation and audit behavior. In adapter mode, a legacy confirmation uses the same ID in the tool audit and the device command. Core acknowledges the dispatch, calls the Home Assistant action and checks the returned entity state with a separate read before recording success. An interrupted or ambiguous action is never automatically repeated. [Home Assistant REST API](https://contributors.home-assistant.io/core/internals/rest-api/#actions)

Existing installations may continue using the legacy integration with both executor settings absent. Once either executor setting is supplied, legacy confirmations require both valid settings and an active grant. Rotate the dedicated paired credential and renew its owner grants together when changing the binding.

### Voice home control for lights and switches

The voice window can list granted lights and switches and handle direct requests such as “Turn on the kitchen light” or “Turn the kitchen light off.” Core uses the latest captured user speech to derive the action. It matches exact entity IDs, Home Assistant's friendly names and owner aliases under `HomeAssistant:Aliases`. Each alias key is a spoken name and its value is a granted entity ID. Voice can resolve only entities in both `HomeAssistant:AllowedEntities` and an effective grant on the paired executor. If a name matches multiple entities, Core returns the candidates. [Home Assistant state endpoints](https://contributors.home-assistant.io/core/internals/rest-api/#states)

Set `preApproved` to `true` when the owner issues a reversible capability grant to allow voice and agent commands within its constraints. Existing grants default to `false`. In voice, a matching grant without pre-approval creates a proposal for manual owner confirmation. An automatically approved spoken action dispatches only in the live tool call. It reports success when the signed receipt confirms the requested state. Core limits automatic Home Assistant dispatches to 20 per device and 20 per entity in a rolling minute, and checks the paid-work pause and privacy state before dispatch.

### Voice light settings, scenes and thermostat targets

Issue a separate version `1.0.0` grant for each capability below. Grant constraints take `allowedEntityIds` for the matching entity type, and those entities also need to appear in `HomeAssistant:AllowedEntities`. The existing on/off grant and owner aliases still apply to on/off requests.

| Capability | Example spoken request | Grant and verification |
| --- | --- | --- |
| `homeassistant.light.set` | “Set the kitchen light brightness to 40 percent”, “Set the kitchen light colour to blue” or “Set the kitchen light colour temperature to 2700 kelvin” | Allowlist `light.*` entities. The command supports 0–100 percent brightness, RGB triplets and 1000–10000 kelvin. Core verifies the relevant reported light attributes, or the off state for zero brightness. [Home Assistant light integration](https://www.home-assistant.io/integrations/light/) |
| `homeassistant.scene.activate` | “Activate the movie night” | Allowlist `scene.*` entities. Core verifies a new Home Assistant activation timestamp after the action. Grant scenes by name to authorize the actions they contain. [Home Assistant scene state](https://contributors.home-assistant.io/core/entities/scene/#states) |
| `homeassistant.climate.set` | “Set the hall thermostat to 21.5 degrees Celsius” | Allowlist `climate.*` entities and specify `minC` and `maxC`, such as 18 and 22. Core converts Celsius when Home Assistant reports Fahrenheit and verifies the reported target. The command sets a target temperature and leaves the HVAC mode alone. [Home Assistant climate integration](https://www.home-assistant.io/integrations/climate/) |

For exact RGB input, say “Set the kitchen light colour to RGB 23 45 67.” For thermostat requests, “degrees” means Celsius. An unavailable or mismatched state produces an unverified outcome, and Core never repeats a scene activation automatically.

### Owner-approved covers, locks and alarms

The owner issues a version `1.0.0` grant for the paired Home Assistant executor. Each grant requires a non-empty `allowedEntityIds` and `allowedActions` list. The entities also need to be in `HomeAssistant:AllowedEntities`. For example, a grant scoped to locking the front door uses `{"allowedEntityIds":["lock.front"],"allowedActions":["lock"]}`. Sensitive grants require a new owner confirmation for every command and reject `preApproved: true`.

| Capability | Allowed actions | Example spoken proposal |
| --- | --- | --- |
| `homeassistant.cover.set` | `open_cover`, `close_cover`, `set_cover_position` | “Close the garage door” or “Set the bedroom blind position to 40 percent” |
| `homeassistant.lock.set` | `lock`, `unlock` | “Lock the front door” |
| `homeassistant.alarm.set` | `alarm_arm_home`, `alarm_arm_away`, `alarm_arm_night`, `alarm_disarm` | “Arm the house alarm in away mode” |

In **Home & tools → Device commands**, review the entity and exact arguments, then approve or reject with the separate owner key. The dashboard sends the displayed proposal's argument hash. The owner can also list commands with `GET /api/device-commands`, which returns up to 50 commands and puts active proposals first. Refresh the dashboard to see the executor's outcome.

If a lock or alarm requires a code, configure it in the owner's protected deployment settings at `HomeAssistant:ActionCodes:<entity_id>`. Core places it only in the Home Assistant action request. The voice tool never requests an action code, and the code stays out of command arguments and receipts. [Home Assistant alarm PIN documentation](https://www.home-assistant.io/integrations/alarm_control_panel/#using-an-alarm-pin-code-in-actions)

The executor sends at most one action per dispatch. It can check state for up to 30 seconds within the command's remaining lifetime, and it requires the expected final state and the exact position where requested. An expired or unverified dispatch does not run again automatically. [Home Assistant cover states](https://www.home-assistant.io/integrations/cover/#cover-states)

## Primary API references

- [OpenAI text generation](https://developers.openai.com/api/docs/guides/text)
- [OpenAI file transcription](https://developers.openai.com/api/docs/guides/speech-to-text)
- [Home Assistant REST API](https://developers.home-assistant.io/docs/api/rest/)
- [Google Calendar events](https://developers.google.com/workspace/calendar/api/v3/reference/events/list)
- [Microsoft Graph calendar view](https://learn.microsoft.com/en-us/graph/api/user-list-calendarview?view=graph-rest-1.0)
- [TfL Unified API](https://tfl.gov.uk/info-for/open-data-users/api-documentation)

## Central GBP authority — Slice 1

The new code requires an explicit `Budget` section before any paid dispatch. The example settings deliberately leave it disabled, with no FX quote or billing multipliers. The private root settings and provider credentials are not automatically changed. Existing USD settings remain additional restrictions; `/api/budget/status` explains this and is the authoritative GBP status.

Trusted fields include `Enabled`, an opaque stable `ScopeId`, `Environment` (`Development` or `Production`), `Version`, explicit `TaxMultiplier` and `SafetyMultiplier` (each at least 1), and `Fx { Source, UsdToGbp, ObservedAt, ExpiresAt }`. Use a public source identifier. Quotes must be positive, observed no later than now, unexpired and valid for at most 24 hours. The £10 development, £100 normal production and £200 maximum production ceilings are enforced by Core.

`Budget:FxAuto:Enabled` defaults to false. Enable it to fetch [ECB euro reference rates](https://www.ecb.europa.eu/stats/policy_and_exchange_rates/euro_reference_exchange_rates/html/index.en.html) at Core startup and every `RefreshHours` (default 6, allowed 1 through 23). Core divides GBP per EUR by USD per EUR and applies `MarginPercent` (default 2). `MaxPublicationAgeDays` defaults to 4 so the latest publication can cover weekends. A valid configured quote takes precedence. An automatic quote lasts 24 hours from the last accepted fetch. A rejected refresh leaves the previously accepted quote available until it expires. The budget status endpoint and AI workflows show the source, age and latest refresh error.

Budget months use UTC. Scope is pinned in durable accounting state after first assignment/dispatch; changing ScopeId cannot reset the allowance. Production increases require an explicit month/policy-bound owner approval, total ceiling above £100 and at most £200, with expiry before the next UTC month. Development cannot receive a production increase. Billing reconciliation is a separate owner operation, not spend approval.

`BudgetManagement { OwnerKey, SubjectId }` is disabled by default. OwnerKey must be a separate secret of at least 32 characters, never the ordinary Core key; SubjectId should be an opaque owner reference. Management requests require both the ordinary Core authentication and `X-Jarvis-Owner-Key` header. Never include the owner key in URLs, Android pairing, browser storage or manifests. This is a personal-server management foundation, not OIDC or general device identity.

No configuration activation, provider-key setup, paid call, deployment or infrastructure change is included in this source implementation. Actual FX/tax inputs and owner credential setup remain explicit operator decisions before activation.

## Review remediation: owner authorization and companion credentials

The separate `BudgetManagement:OwnerKey` / `SubjectId` credential now also protects privacy export/erase/reopen/retention-apply, device verification/enrolment/revocation and home-action confirmation. It is independent of `Budget.Enabled`: sensitive administrative operations fail closed until owner setup exists. The optional browser Owner key field keeps this credential in tab memory and clears it on disconnect. Supply it only on your trusted Core computer, never to Android. Ordinary Core authentication is still required alongside the owner header.

Companion credentials are random `jdv1_` secrets, stored only as SHA-256 hashes in existing runtime persistence and excluded from privacy exports. Owner-authorized `POST /api/devices/{id}/credential` returns a new secret once; calling it again rotates the secret and fences existing audio. `DELETE` on the same route revokes it. Revocation invalidates pending voice tickets and the active device lease; REST checks the database on each request. Voice ticket creation also rejects stale authenticated requests after rotation. Companion authorization is a fixed server-side audio policy, not caller-supplied grants.

Migration before any WAN use:

1. Configure a strong separate owner credential on Core, without enabling paid providers. Keep deployment private.
2. Register the Android audio-only device from the trusted browser/operator client, test its hardware and confirm it with the owner-authorized verify route. Administrative confirmation itself is not proof of a hardware test.
3. Issue its device credential using the owner header. Put only that device secret, trusted Core URL and certificate pin in the phone's pairing details. The audio client adopts the credential-bound device ID returned by its filtered device list.
4. Migrate every phone, then rotate the old shared Core key so previously paired broad credentials cannot continue to act as legacy browsers. Update trusted browsers with the new Core key. Merely deploying scoped-auth code does not remove old shared credentials from phones.
5. Verify denial of privacy/tools/agents/other-device routes, revoke/re-pair behavior and real-device audio on the intended network before wider access.

The legacy Core key remains a broad browser credential for compatibility. SignalR WebSocket query authentication now requires a one-use, 30-second ticket from `POST /api/hubs/ticket`; raw query keys are rejected. Header credentials remain compatible with .NET callers. No credentials, private settings or running deployment were changed by source remediation.

### Reviewed media and model profiles

Each `AI:{profile}` requires its own `RateCardDate` (not future and no more than 90 days old) and `RateCardModel` equal to its selected `Model`. Text profiles accept `MaxOutputTokens` from 1 to 32768. OpenAI defaults remain 1500 for Chat and 2048 for other text profiles. Anthropic defaults are 8192 for Chat/Coach and 16384 for other profiles, including `PaidAI:MaxOutputTokens` for LegacyInterview. Thinking consumes this output allowance. An explicitly configured smaller cap is respected. The reservation follows the selected cap, including every configured fallback attempt.

The Release 0.2 example selects `gpt-6-luna` for Chat and Coach. To activate it, set each profile's model and its matching reviewed rate-card model, date and rates. For an OpenAI Chat profile, keep `OpenAI:Model` equal to `AI:Chat:Model`. OpenAI lists Luna as the successor to the deprecated GPT-4.1 mini snapshot. [OpenAI deprecations](https://developers.openai.com/api/docs/deprecations)

Voice requires explicit `AudioInputUsdPerMillion`, `TextInputUsdPerMillion`, `CachedAudioInputUsdPerMillion`, `CachedTextInputUsdPerMillion`, `AudioOutputUsdPerMillion`, `TextOutputUsdPerMillion`, `TranscriptionUsdPerMinute`, `TranscriptionModel` and matching `TranscriptionRateCardModel`. Audio rates cannot be lower than the corresponding text rate; cached rates must be positive and no greater than uncached rates. No live rates are supplied by this remediation. Keep profiles stable while calls are active; immutable cross-provider attempt routing remains unfinished.

Paid file transcription accepts WebM, MP4/M4A and WAV only, bounds actual input to 10 MB, decodes at most 180 seconds of mono 16 kHz PCM and sends a fresh WAV. Duration metadata does not determine its reservation. Missing granular billed usage retains the full conservative reservation for reconciliation.

### Encrypted Windows database backups

New backups use Windows CurrentUser DPAPI and the `.dump.dpapi` extension, with ACL restricted to that user and SYSTEM. Restore decrypts into a temporary file and removes it afterward; legacy plaintext dumps remain accepted. This implementation has a 128 MB plaintext limit and fails closed for larger dumps or unsupported encryption platforms.

Recovery requires the originating Windows user/profile's DPAPI material. Preserve that recovery access and verify restore before relying on a copied backup on another computer; a copied encrypted dump alone is insufficient. Existing plaintext backups, directory sync settings and Windows profile recovery were not changed. The isolated synthetic deployment test verifies restore on the current account only.

### Owner rollout checklist after review fixes

1. Configure `BudgetManagement:OwnerKey` (separate from `Jarvis:ApiKey`) and opaque `SubjectId` on the trusted Core host.
2. Verify each phone's audio-only registration, issue its credential using owner-authorized `POST /api/devices/{id}/credential`, and pair the phone with the returned `jdv1_` secret. Export any needed legacy drafts before changing pairing identity. After all phones are migrated, rotate the legacy Core key and update trusted browsers. Keep Core restricted to the LAN until these first two steps are complete.
3. Configure `JARVIS_FRONTEND_REPOSITORY`, `JARVIS_MOBILE_REPOSITORY`, any required private repository read token and required CI checks. Execute the workflow and record its actual result in `docs/VERIFICATION.md`.
4. Record real-device checks for earbuds, screen-off, network toggles, incoming calls, browser/phone handoff and simultaneous audio starts on two devices. Automated fixtures do not establish hardware acceptance.

This checklist prepares owner actions. Source remediation does not perform credential rotation, private configuration edits or deployment.

### Text routing and attempt records

Chat, coaching, agents and legacy interview briefs use the same normalized reasoning port. OpenAI Responses and Anthropic Messages codecs are installed. `AI:{profile}:Provider` defaults to `openai` for existing profiles. Selecting another provider also requires an explicit `AllowedDataClasses` array (`Public` and/or `Personal`), its reviewed model/rates/date and its credential. Existing Anthropic briefs retain the `PaidAI` and `Anthropic` configuration layout. No destination is selected automatically when a route cannot supply the requested capability.

Grounded public search requires the Research profile, Public data class, an installed adapter advertising that capability and positive `AI:Research:SearchUsdPerCall`. The public example leaves this price unset. Anthropic's installed codec supports conversation and JSON reasoning; grounded search is refused there. Future installed codecs can register the same interface; their credentials use `Providers:{provider}:ApiKey` unless a legacy credential mapping exists.

Every text attempt enters a shared durable reservation before adapter dispatch. Attempt records contain provider/model, profile, capability, data class, consent, reviewed rate revision, reservation ID and outcome. Keys and prompt text are excluded from these records. The selected model, key and prices are captured for that attempt; configuration changes during the response cannot change its billing calculation. Known completed responses can be returned from cache. Pending, failed or uncertain attempts cannot be automatically replayed; old paid workflow claims can reuse a matching completed result but cannot silently pay again when it is unavailable.

Source deletion fences subsequent publication, clears cached text and removes the attempt's content fingerprint. The content-free route/charge record remains alongside the existing financial ledger. Full Core erasure removes attempt records while retaining the separately defined financial commitments. Attempt fingerprints also expire with the seven-day disposable-cache policy. Hardware, private configuration and paid-provider acceptance remain separate operator steps.

### Anthropic adaptive output, caching and refusal fallback

The LegacyInterview example now selects `claude-sonnet-5-5`; its prices and review date are unset until reviewed for that model. Existing private settings are never rewritten. Routing Coach to Sonnet or deep tasks to Opus remains an owner configuration decision, with explicit `AllowedDataClasses` for each new destination. Research and realtime voice keep their existing codecs.

Under `AI:{profile}` (or `PaidAI` for LegacyInterview), `PromptCachingEnabled` defaults to false. Enabling it requires positive, reviewed `CacheWriteUsdPerMillion` and `CacheReadUsdPerMillion` alongside the ordinary rates. Only the stable system instruction prefix receives an explicit five-minute cache breakpoint. User messages receive none. Short prompts may not meet the provider's cache threshold. Ordinary input, cache creation, cache reads and output are charged separately; thinking output is included. An unexpected one-hour write is held for reconciliation because its price has not been reviewed. [Anthropic caching](https://platform.claude.com/docs/en/build-with-claude/prompt-caching)

`Fallbacks` defaults to an empty array. To opt in, add up to three entries, each with `Model`, matching `RateCardModel`, recent `RateCardDate`, `InputUsdPerMillion`, `OutputUsdPerMillion`, and both cache rates when caching is enabled. Each entry's model must be distinct from the primary and the other entries, and must be an allowed fallback target for the primary according to Anthropic's Models API. Configure exact model IDs as listed by the Models API, including a resolved or dated ID where applicable. If any returned model ID differs from its reviewed configured ID, JARVIS holds the reservation, discards the response and pauses further paid work until reconciled. Each entry inherits the profile's output cap and approved data classes. Haiku 5.5 does not support a server-side fallback list; leave its list empty. The sanitized shape below intentionally cannot dispatch until its null values are reviewed and filled:

```json
{
  "Provider": "anthropic",
  "Model": "claude-sonnet-5-5",
  "RateCardModel": "claude-sonnet-5-5",
  "RateCardDate": "",
  "InputUsdPerMillion": null,
  "OutputUsdPerMillion": null,
  "MaxOutputTokens": 8192,
  "AllowedDataClasses": ["Personal"],
  "PromptCachingEnabled": false,
  "CacheWriteUsdPerMillion": null,
  "CacheReadUsdPerMillion": null,
  "Fallbacks": [{
    "Model": "claude-sonnet-5",
    "RateCardModel": "claude-sonnet-5",
    "RateCardDate": "",
    "InputUsdPerMillion": null,
    "OutputUsdPerMillion": null,
    "CacheWriteUsdPerMillion": null,
    "CacheReadUsdPerMillion": null
  }]
}
```

Configured fallback uses one HTTP request with an explicit `fallbacks` list and beta header `server-side-fallback-2026-07-01`. This bounds possible destinations and costs. No client retry is sent for a refusal, error, timeout or overload. The adapter prices `usage.iterations` at each model's captured rates, without also charging the final top-level usage. Known zero-output, non-billable refusal categories cost zero; unknown categories require reconciliation. `ProviderAttempts.Route` retains the requested route, `ServedRoute` records the returned model, and `ModelUsage` records each model's token classes and charge. Cached completion returns the same served route. [Anthropic refusal and fallback accounting](https://platform.claude.com/docs/en/build-with-claude/refusals-and-fallback)

The refusal billing category schedule was last reviewed on **2026-11-17** alongside this adapter's text rate-card handling. Recheck [How refusals are billed](https://platform.claude.com/docs/en/build-with-claude/refusals-and-fallback#how-refusals-are-billed) whenever reviewing the configured prices; Anthropic may change which categories are billed before output.

The request uses API version `2026-10-21` and omits removed sampling parameters. Thinking stays at the model default. `max_tokens` and `refusal` stops settle known usage but discard partial output and cannot be replayed automatically. Unknown models, missing fallback accounting or unpriced token classes retain the conservative reservation and block new paid work pending reconciliation. [Anthropic version guidance](https://platform.claude.com/docs/en/api/versioning)

Home Assistant now requires HTTPS outside loopback unless `HomeAssistant:AllowInsecureLan=true` is explicitly set by the operator. The public example defaults this exception to false. This does not encrypt an insecure LAN link; TLS is preferred.

Legacy draft migration precaution: before replacing a phone's existing connection credential, explicitly export any legacy encrypted drafts using the current trusted connection. Legacy cache discovery is keyed by the old URL/credential identity; changing the credential does not delete those files, but the new connection will not automatically discover them. Do not replay or upload them during pairing. This remediation did not alter existing encrypted draft files or their keys.

Draft-preservation follow-up: ordinary 401 credential revocation and malformed/missing privacy metadata stop audio and clear the active credential but preserve encrypted legacy drafts. Confirmed Core erasure or a valid changed privacy generation triggers cleanup for the current legacy identity. A revoked device credential querying an erased Core receives a 401 with the bounded `coreErased` marker and epoch so the phone can distinguish explicit erasure from ordinary key rotation. No personal data or credential material is returned by that error. This does not silently replay, migrate or export any draft.

## Independent pause controls

`AgentControls` adds nullable `PaidAiPaused` and `AgentsPaused`. Missing new values inherit the historical `Paused` flag, so existing persisted settings/old requests keep their meaning. A management write normalizes both new flags and clears the legacy flag. Paid admission/approval uses the paid flag; scheduling, claims, completion and notification delivery use the agent flag. The dashboard exposes separate controls. Neither flag revokes an existing audio lease or blocks unpaid local audio. Already reserved voice windows keep their bounded lease; the next paid reservation is refused while paid AI is paused. Explicit audio Stop, handoff, device revocation and privacy erasure retain their fences. Erasure keeps both work types paused on reopen.
