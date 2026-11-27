# PF-05 drill caller design (proposed, 27 November 2026)

Status: **PROPOSED, needs Codex design review before implementation.** No source change accompanies this document.

## What exists (merged on `development` 8475241)
- `ForgeTurnQualityGate` v3: scored, retry or practice-only. Pronunciation is never assessed.
- `ForgeLanguageProfiles`: en-GB, fr-FR, es-ES, with correction after each turn by default.
- `ForgeTranscriptionProtocol` plus `OpenAiProvider.TranscribeForgeTurnAsync`. Forge-attributed, with the turn ID as reservation ID, a required post-reservation ownership/pause fence, zero settlement on pre-dispatch refusal, and conservative accounting after dispatch.
- `ForgeSpeakingTurn.RunAsync`: a free audio-only preflight, then transcription and a post-result ownership recheck, then the gate.
- Owner decision: keep `gpt-transcribe`. Turns are **practice only** until a calibrated quality signal is reviewed.

## Proposed turn loop (ADR 0025 turn-based mode, not realtime)
1. **Objective and prompt.** The drill picks the next item from authored, versioned, locale-tagged content. Prompts are fixed text, with no model call to generate them.
2. **Speak the prompt.** See decision D1.
3. **Listen.** Capture one turn's PCM over the **existing** companion/browser audio channel under the existing lease. Capture ends on owner stop, silence end-pointing (the existing turn detection), or the 180 s bound.
4. **Run the turn.** `ForgeSpeakingTurn.RunAsync(pcm, session, turnId, locale, current)`, where `current = !client.StopRequested && ownership.CurrentAsync(lease) && source/privacy epoch unchanged`. This is the same composition as `RealtimeVoiceNegotiation.CurrentAsync`; the paid pause is already checked inside the provider call.
5. **Fence every outcome** (Codex, #56 review). Immediately before publishing or recording *any* result, including the free audio-only retry, re-run `current`. If it fails, drop the result silently: no stale retry prompt, and no record after stop.
6. **Correction.** See decision D2. Correction timing comes from the profile default (`AfterEachTurn`).
7. **Checkpoint.** Deferred to PF-04 (below). Until then the loop is ephemeral: nothing is stored beyond the existing financial reservation.

## Identity and retry
- **Turn IDs** are deterministic from `(drill session, item, attempt ordinal)`, following the existing `VoicePracticeService.Operation` hashing pattern. They are not random, so a reconnect can't silently re-dispatch.
- A **retry** is an explicit new user attempt with the next ordinal, after the user speaks again. It is **never** an automatic re-send of the same captured PCM (Codex, #55).
- A 409 on a reused turn ID is surfaced as "already handled", not retried.

## Decisions needed
- **D1, prompt speech.** Options:
  - (a) Device TTS on browser or Android. No AI cost. Android stays audio-only (it plays audio and holds no business logic).
  - (b) Server TTS through a new Forge-attributed provider path. This is a new paid path with its own review.
  - (c) Text-only prompts in the browser to start.
  - **Proposal:** (a) for prompts, with (c) as the fallback.
- **D2, correction source.** Options:
  - (a) Authored model answers and rubric hints only. No AI cost, no hallucination risk.
  - (b) A small text-model correction through the existing reasoning gateway with Forge attribution. Paid, needs grounding/hallucination review, and would be unscored under the practice-only decision.
  - **Proposal:** (a) for the first slice.
- **D3, persistence.** Per Codex (PR53 and #56 reviews): reuse the canonical attempt path with an **unscored/unknown** outcome, not score 0 and no SRS advancement, once PF-04 / PR45 integration permits. No ForgeAttempts or parallel history. **Blocked** until then.
- **D4, surface.** Which client starts a drill first:
  - (a) the browser, where turn capture is easiest to test;
  - (b) the Android companion, under the existing foreground/lease rules and with a real owner hardware check.
  - **Proposal:** browser first.

## Implementation slices after approval
1. **Content.** A small authored fr-FR starter set as a versioned code catalogue with model answers. No Domain Registry install or activation, keeping clear of the owner's French/Spanish auto-active decision.
2. **Drill session (ephemeral).** Start, next item, capture, run, then publish a fenced result over the existing session channel behind `Forge:Enabled`. Free preflight retries and practice-only outcomes. No storage.
3. **Device-TTS prompt playback** (if D1a), on the browser client.
4. **Persistence** via PF-04, after PR45.

## Out of scope
Paid pilot, setting the allowance, live config, model change, pronunciation scoring, Android native UI beyond audio, and French/Spanish domain activation.
