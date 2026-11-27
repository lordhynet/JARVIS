using System.Buffers.Binary;
using Jarvis.Api.Voice;
using Jarvis.Orchestrator.Coaching;
using Jarvis.Orchestrator.Forge;
using R = Jarvis.Orchestrator.Forge.ForgeTurnUnscorableReason;

namespace Jarvis.Tests;

public sealed class ForgeTurnQualityTests
{
    private const int Rate = 24000;
    private static ForgeSpeechActivity Active(double active, double captured = 5) => new(captured, active, ForgeSpeechActivity.PcmActiveMethod);

    // ---- Real PCM through the existing meter (review Q1/Q2) ----

    [Fact]
    public void A_short_signal_padded_with_silence_is_too_short_not_four_seconds_of_speech()
    {
        var pcm = Pcm(4, (1.9, .1, Tone));
        var activity = SpeechAudioMeter.AnalyseActivity(pcm, Rate)!;
        Assert.Equal(4, activity.CapturedSeconds); Assert.Equal(.1, activity.ActiveSeconds, 3);
        var result = ForgeTurnQualityGate.Assess("fr-FR", activity, "Bonjour", .9, "fr");
        Assert.Equal(R.TooShort, result.Reason); Assert.True(result.RetryOffered);
        // Delivery metrics keep their existing meaning: whole window, internal pauses only.
        var timing = SpeechAudioMeter.Analyse(pcm, Rate)!;
        Assert.Equal(4, timing.AudioSeconds); Assert.Equal(0, timing.QuietSeconds); Assert.Equal(SpeechDelivery.PcmQuietMethod, timing.Method);
    }

    [Fact]
    public void Words_per_second_uses_active_speech_not_padded_capture_time()
    {
        var activity = SpeechAudioMeter.AnalyseActivity(Pcm(4, (1.5, 1, Tone)), Rate)!;
        Assert.Equal(1, activity.ActiveSeconds, 3);
        Assert.Equal(R.ImplausibleTranscript, ForgeTurnQualityGate.Assess("fr-FR", activity,
            "un deux trois quatre cinq six sept huit neuf dix", .9, "fr").Reason);
    }

    [Fact]
    public void Internal_pauses_are_excluded_from_active_speech()
    {
        var activity = SpeechAudioMeter.AnalyseActivity(Pcm(5, (.5, 1, Tone), (2.5, 1, Tone)), Rate)!;
        Assert.Equal(2, activity.ActiveSeconds, 3);
    }

    [Fact]
    public void Broadband_noise_with_a_plausible_transcript_and_no_confidence_is_unscorable()
    {
        var activity = SpeechAudioMeter.AnalyseActivity(Pcm(3, (0, 3, Noise)), Rate)!;
        Assert.Equal(3, activity.ActiveSeconds, 3); // Noise is energetic; energy is not clear speech.
        var result = ForgeTurnQualityGate.Assess("fr-FR", activity, "Bonjour je voudrais parler", null, "fr");
        Assert.False(result.Scorable); Assert.Equal(R.QualityEvidenceMissing, result.Reason); Assert.True(result.RetryOffered);
        Assert.Equal(R.LowConfidence, ForgeTurnQualityGate.Assess("fr-FR", activity, "Bonjour je voudrais parler", .3, "fr").Reason);
    }

    [Fact]
    public void Silence_yields_no_activity_and_no_measured_speech()
    {
        Assert.Null(SpeechAudioMeter.AnalyseActivity(Pcm(2), Rate));
        Assert.Equal(R.NoMeasuredSpeech, ForgeTurnQualityGate.Assess("fr-FR", null, "Bonjour", .9).Reason);
    }

    [Fact]
    public void Explicit_clear_speech_evidence_path_passes_without_ever_claiming_pronunciation()
    {
        var activity = SpeechAudioMeter.AnalyseActivity(Pcm(4, (.5, 3, Tone)), Rate)!;
        var result = ForgeTurnQualityGate.Assess("fr-FR", activity, "Je voudrais réserver une table pour deux personnes ce soir.", .92, "fr");
        Assert.True(result.Scorable); Assert.Null(result.Reason); Assert.False(result.RetryOffered);
        Assert.False(result.PronunciationAssessable);
        Assert.Equal(ForgeTurnQualityGate.RuleVersion, result.RuleVersion);
        Assert.Equal(3, result.ActiveSpeechSeconds!.Value, 3); Assert.Equal(10, result.Words);
    }

    // ---- Threshold boundaries (frame-aligned, 20 ms) ----

    [Theory]
    [InlineData(.78, R.TooShort)]
    [InlineData(.80, null)]
    public void Minimum_speech_boundary_is_inclusive(double seconds, R? expected)
    {
        var activity = SpeechAudioMeter.AnalyseActivity(Pcm(3, (1, seconds, Tone)), Rate)!;
        Assert.Equal(seconds, activity.ActiveSeconds, 3);
        Assert.Equal(expected, ForgeTurnQualityGate.Assess("en-GB", activity, "Yes", .9).Reason);
    }

    [Theory]
    [InlineData(13, null)]   // 6.5 words per second over 2 s
    [InlineData(14, R.ImplausibleTranscript)]
    public void Plausibility_ceiling_boundary_is_inclusive(int words, R? expected) =>
        Assert.Equal(expected, ForgeTurnQualityGate.Assess("en-GB", Active(2), string.Join(' ', Enumerable.Repeat("word", words)), .9).Reason);

    [Theory]
    [InlineData(.6, null)]
    [InlineData(.59, R.LowConfidence)]
    public void Confidence_boundary_is_inclusive(double confidence, R? expected) =>
        Assert.Equal(expected, ForgeTurnQualityGate.Assess("en-GB", Active(2), "Hello there", confidence).Reason);

    // ---- Invalid timing is unscorable, never clamped ----

    public static TheoryData<ForgeSpeechActivity> InvalidActivity => new()
    {
        new(3, 50, ForgeSpeechActivity.PcmActiveMethod),          // active exceeds capture (cf. quiet −50)
        new(3, -1, ForgeSpeechActivity.PcmActiveMethod),
        new(double.NaN, 1, ForgeSpeechActivity.PcmActiveMethod),
        new(3, double.PositiveInfinity, ForgeSpeechActivity.PcmActiveMethod),
        new(0, 0, ForgeSpeechActivity.PcmActiveMethod),
        new(181, 2, ForgeSpeechActivity.PcmActiveMethod),
        new(3, 2, SpeechDelivery.PcmQuietMethod)                  // a delivery timing must not be relabelled as activity
    };

    [Theory]
    [MemberData(nameof(InvalidActivity))]
    public void Malformed_activity_is_unscorable_and_reports_no_speech_duration(ForgeSpeechActivity activity)
    {
        var result = ForgeTurnQualityGate.Assess("fr-FR", activity, "Bonjour à tous", .9);
        Assert.Equal(R.InvalidTiming, result.Reason); Assert.True(result.RetryOffered); Assert.Null(result.ActiveSpeechSeconds);
    }

    // ---- Transcript and language evidence ----

    [Theory]
    [InlineData("   ", null, R.NoTranscript)]
    [InlineData("... ?! —", null, R.NoTranscript)]
    [InlineData("I would like a table for two", "en", R.LanguageMismatch)]
    public void Transcript_and_language_evidence_failures_offer_a_retry(string text, string? detected, R expected)
    {
        var result = ForgeTurnQualityGate.Assess("fr-FR", Active(3), text, .9, detected);
        Assert.Equal(expected, result.Reason); Assert.True(result.RetryOffered); Assert.False(result.PronunciationAssessable);
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("FR-ca")]
    [InlineData("fr_BE")]
    [InlineData(null)]
    [InlineData("")]
    public void Regional_or_missing_language_hints_do_not_turn_an_accent_into_a_mismatch(string? detected) =>
        Assert.True(ForgeTurnQualityGate.Assess("fr-FR", Active(3), "Je suis prêt", .9, detected).Scorable);

    [Theory]
    [InlineData("fr")]
    [InlineData("fr-fr")]
    [InlineData("de-DE")]
    [InlineData("")]
    public void Every_turn_requires_an_explicit_supported_starter_locale(string locale) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ForgeTurnQualityGate.Assess(locale, Active(3), "Bonjour", .9));

    [Theory]
    [InlineData(-.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    public void Out_of_range_confidence_is_rejected_rather_than_clamped(double confidence) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ForgeTurnQualityGate.Assess("en-GB", Active(3), "Hello there", confidence));

    [Fact]
    public void Assessment_is_deterministic() =>
        Assert.Equal(ForgeTurnQualityGate.Assess("en-GB", Active(2.1), "Let me explain the trade-off", .7, "en-GB"),
            ForgeTurnQualityGate.Assess("en-GB", Active(2.1), "Let me explain the trade-off", .7, "en-GB"));

    [Theory]
    [InlineData("l'école est fermée", 3)]
    [InlineData("  un   deux\ttrois\n", 3)]
    [InlineData("— ... !", 0)]
    [InlineData(null, 0)]
    public void Word_count_ignores_punctuation_only_tokens(string? text, int expected) =>
        Assert.Equal(expected, ForgeTurnQualityGate.Words(text));

    // ---- Synthetic PCM: silence with signal segments (start, duration, generator). Not speech and not calibration. ----

    private static short Tone(int i) => (short)(6000 * Math.Sin(2 * Math.PI * 220 * i / Rate));
    private static short Noise(int i) { var x = (uint)i * 1103515245u + 12345u; x ^= x >> 13; x *= 2654435761u; return (short)((int)(x % 12001) - 6000); }

    private static byte[] Pcm(double seconds, params (double Start, double Length, Func<int, short> Signal)[] segments)
    {
        var samples = (int)Math.Round(Rate * seconds); var bytes = new byte[samples * 2];
        foreach (var (start, length, signal) in segments)
            for (var i = (int)Math.Round(start * Rate); i < (int)Math.Round((start + length) * Rate); i++)
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), signal(i));
        return bytes;
    }
}
