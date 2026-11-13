# Connections and devices

Use .NET user secrets for local development, or environment variables for containers.
Never commit real keys or tokens. `dotnet user-secrets set --project src/Jarvis.Api "Setting:Name" "value"`
sets a local value; the corresponding environment name is `Setting__Name`.

| Setting | Purpose |
|---|---|
| `Jarvis:ApiKey` | Shared operator credential used by the companion and API clients |
| `Jarvis:StorageProvider` | `Postgres` or `InMemory`; default is `InMemory` |
| `ConnectionStrings:Jarvis` | PostgreSQL connection string; required in Postgres mode |
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

The container variable names accepted by the supplied Compose file are listed in
`compose.app.yaml`. Model names are deliberately not guessed. Cloud requests are bounded
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

## Primary API references

- [OpenAI text generation](https://developers.openai.com/api/docs/guides/text)
- [OpenAI file transcription](https://developers.openai.com/api/docs/guides/speech-to-text)
- [Home Assistant REST API](https://developers.home-assistant.io/docs/api/rest/)
- [Google Calendar events](https://developers.google.com/workspace/calendar/api/v3/reference/events/list)
- [Microsoft Graph calendar view](https://learn.microsoft.com/en-us/graph/api/user-list-calendarview?view=graph-rest-1.0)
- [TfL Unified API](https://tfl.gov.uk/info-for/open-data-users/api-documentation)
