using Jarvis.Contracts;

namespace Jarvis.Orchestrator.Forge;

public enum ForgeTurnUnscorableReason
{
    NoMeasuredSpeech = 1,
    TooShort,
    NoTranscript,
    LowConfidence,
    ImplausibleTranscript,
    LanguageMismatch,
    InvalidTiming,
    QualityEvidenceMissing
}

// Above-threshold PCM duration measured by the existing Core meter. It excludes leading, trailing and internal silence,
// unlike SpeechTiming (whose QuietSeconds counts only internal pauses). Energy alone does not distinguish speech from noise.
public sealed record ForgeSpeechActivity(double CapturedSeconds, double ActiveSeconds, string Method)
{
    public const string PcmActiveMethod = "core-pcm-active-v1";
    public const double MaximumCapturedSeconds = 180;
    public bool Valid => Method == PcmActiveMethod && double.IsFinite(CapturedSeconds) && double.IsFinite(ActiveSeconds) &&
        CapturedSeconds is > 0 and <= MaximumCapturedSeconds && ActiveSeconds >= 0 && ActiveSeconds <= CapturedSeconds;
}

// The outcome of the audio-quality gate that precedes any Forge speaking assessment (ADR 0025, PF-05/07, original §12 item 6).
// An unscorable turn offers a retry and is never a learning failure. Pronunciation is never judged here:
// a transcript and PCM energy cannot establish phoneme accuracy, so PronunciationAssessable is always false
// until a calibrated adapter (PF-20) exists.
public sealed record ForgeTurnQuality(string RuleVersion, bool Scorable, ForgeTurnUnscorableReason? Reason,
    bool RetryOffered, double? ActiveSpeechSeconds, int Words)
{
    public bool PronunciationAssessable => false;
}

// Pure and deterministic: no storage, provider, clock or locale-specific parsing. Thresholds are uncalibrated policy
// proposals; RuleVersion is recorded so later evidence states which rule judged a turn.
public static class ForgeTurnQualityGate
{
    public const string RuleVersion = "forge-turn-quality-v2";
    public const double MinimumSpeechSeconds = .8;
    public const double MinimumConfidence = .6;
    // Fluent speech rarely exceeds ~4 words/second; far above that suggests a transcript invented from noise.
    public const double MaximumWordsPerSpeechSecond = 6.5;
    public static readonly IReadOnlySet<string> StarterLocales = new HashSet<string>(StringComparer.Ordinal) { "en-GB", "fr-FR", "es-ES" };

    // Eligibility needs explicit clear-speech evidence. PCM energy plus a plausible transcript cannot certify a turn, because
    // noise is energetic and transcribers invent text, so a missing transcription confidence is unscorable, not assumed good.
    public static ForgeTurnQuality Assess(string expectedLocale, ForgeSpeechActivity? activity, string? transcript,
        double? transcriptionConfidence, string? detectedLanguage = null)
    {
        if (!StarterLocales.Contains(expectedLocale))
            throw new ArgumentOutOfRangeException(nameof(expectedLocale), "An explicit supported practice locale is required for every turn.");
        if (transcriptionConfidence is { } c && (double.IsNaN(c) || c is < 0 or > 1))
            throw new ArgumentOutOfRangeException(nameof(transcriptionConfidence), "Confidence must be between 0 and 1.");

        var words = Words(transcript);
        var speech = activity is { Valid: true } a ? a.ActiveSeconds : (double?)null;
        ForgeTurnQuality Result(ForgeTurnUnscorableReason? reason) => new(RuleVersion, reason is null, reason, reason is not null, speech, words);

        // Order: malformed or missing audio evidence, then transcript evidence, then quality, then consistency between them.
        if (activity is not null && !activity.Valid) return Result(ForgeTurnUnscorableReason.InvalidTiming);
        if (speech is null or 0) return Result(ForgeTurnUnscorableReason.NoMeasuredSpeech);
        if (speech < MinimumSpeechSeconds) return Result(ForgeTurnUnscorableReason.TooShort);
        if (words == 0) return Result(ForgeTurnUnscorableReason.NoTranscript);
        if (transcriptionConfidence is null) return Result(ForgeTurnUnscorableReason.QualityEvidenceMissing);
        if (transcriptionConfidence < MinimumConfidence) return Result(ForgeTurnUnscorableReason.LowConfidence);
        if (words / speech > MaximumWordsPerSpeechSecond) return Result(ForgeTurnUnscorableReason.ImplausibleTranscript);
        if (detectedLanguage is { Length: > 0 } detected && !SameLanguage(detected, expectedLocale))
            return Result(ForgeTurnUnscorableReason.LanguageMismatch);
        return Result(null);
    }

    // Provider language hints are often bare ("fr") or differently cased ("FR-ca"); compare the primary subtag only.
    // Region variants are configured elsewhere and must not turn an accent into a mismatch.
    private static bool SameLanguage(string detected, string expected) =>
        string.Equals(Primary(detected), Primary(expected), StringComparison.OrdinalIgnoreCase);
    private static string Primary(string tag) => tag.Trim().Split('-', '_')[0];

    // Whitespace-separated tokens containing at least one letter or digit; punctuation-only tokens are not words.
    // French elisions ("l'école") count as one token, which is conservative for the plausibility ceiling.
    public static int Words(string? text) => string.IsNullOrWhiteSpace(text) ? 0 :
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Count(token => token.Any(char.IsLetterOrDigit));
}
