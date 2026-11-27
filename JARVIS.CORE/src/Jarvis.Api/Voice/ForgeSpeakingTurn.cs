using Jarvis.Api.Integrations;
using Jarvis.Orchestrator.Forge;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Api.Voice;

// The outcome of one Forge speaking turn. Transcript is null when the audio itself was not worth a paid call.
internal sealed record ForgeSpeakingTurnResult(ForgeTurnQuality Quality, ForgeTurnTranscript? Transcript, bool ProviderCalled);

// One turn of a Forge drill (PF-05): measure the captured audio, transcribe only when it can possibly qualify, and judge it
// with the shared quality gate. No storage, route, scoring, retry or caller here; a reviewed drill caller owns those.
internal sealed class ForgeSpeakingTurn(OpenAiProvider provider)
{
    private static readonly HashSet<ForgeTurnUnscorableReason> AudioOnlyRefusals =
        [ForgeTurnUnscorableReason.InvalidTiming, ForgeTurnUnscorableReason.NoMeasuredSpeech, ForgeTurnUnscorableReason.TooShort];

    // `current` must be bound to the real captured lease/source identity. It fences dispatch inside the provider call and is
    // rechecked here before a late transcript is used: a stale result is refused (409), never returned to the caller.
    public async Task<ForgeSpeakingTurnResult> RunAsync(byte[] pcm, Guid sessionId, Guid turnId, string locale,
        Func<CancellationToken, Task<bool>> current, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        ArgumentNullException.ThrowIfNull(current);
        Guard.Require(ForgeLanguageProfiles.IsStarter(locale), 400, "A Forge turn needs an explicit supported practice locale.");

        // Silence, too little speech or unmeasurable audio cannot pass the gate whatever a transcriber returns, so they are
        // refused with a retry before any reservation or provider I/O: no money is spent on audio that cannot qualify.
        var activity = SpeechAudioMeter.AnalyseActivity(pcm, Jarvis.Orchestrator.Runtime.VoiceWindowLimits.SampleRate);
        var audioOnly = ForgeTurnQualityGate.Assess(locale, activity, null, null);
        if (audioOnly.Reason is { } reason && AudioOnlyRefusals.Contains(reason))
            return new(audioOnly, null, ProviderCalled: false);

        var transcript = await provider.TranscribeForgeTurnAsync(pcm, sessionId, turnId, locale, current, ct);
        Guard.Require(await current(ct), 409, "Audio ownership changed while this practice turn was transcribed; its result was discarded.");
        var quality = ForgeTurnQualityGate.Assess(locale, activity, transcript.Text, transcript.Confidence, transcript.DetectedLanguage,
            transcript.QualityEvidenceAvailable);
        return new(quality, transcript, ProviderCalled: true);
    }
}
