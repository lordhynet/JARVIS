using System.Buffers.Binary;
using Jarvis.Api.Voice;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Forge;
using R = Jarvis.Orchestrator.Forge.ForgeTurnUnscorableReason;

namespace Jarvis.Tests;

public sealed class ForgeTurnQualityTests
{
    private static SpeechTiming Timing(double audio, double quiet = 0) => new(audio, quiet, "fixture");

    [Fact]
    public void Clear_French_turn_is_scorable_without_ever_claiming_pronunciation()
    {
        var result = ForgeTurnQualityGate.Assess("fr-FR", Timing(4, 1), "Je voudrais réserver une table pour deux personnes ce soir.", .92, "fr");
        Assert.True(result.Scorable); Assert.Null(result.Reason); Assert.False(result.RetryOffered);
        Assert.False(result.PronunciationAssessable);
        Assert.Equal(ForgeTurnQualityGate.RuleVersion, result.RuleVersion);
        Assert.Equal(3, result.SpeechSeconds); Assert.Equal(10, result.Words); Assert.True(result.ConfidenceKnown);
    }

    public static TheoryData<SpeechTiming?, string?, double?, string?, R> Unscorable => new()
    {
        { null, "Bonjour", .9, null, R.NoMeasuredSpeech },
        { new(2, 2, "fixture"), "Bonjour", .9, null, R.NoMeasuredSpeech },
        { new(double.NaN, 0, "fixture"), "Bonjour", .9, null, R.NoMeasuredSpeech },
        { new(1, .5, "fixture"), "Bonjour", .9, null, R.TooShort },
        { new(3, 0, "fixture"), "   ", .9, null, R.NoTranscript },
        { new(3, 0, "fixture"), "... ?! —", .9, null, R.NoTranscript },
        { new(3, 0, "fixture"), "Bonjour à tous", .4, null, R.LowConfidence },
        { new(1, 0, "fixture"), "un deux trois quatre cinq six sept huit", .9, null, R.ImplausibleTranscript },
        { new(3, 0, "fixture"), "I would like a table for two", .9, "en", R.LanguageMismatch }
    };

    [Theory]
    [MemberData(nameof(Unscorable))]
    public void Poor_evidence_is_unscorable_with_a_retry_and_never_a_failure(SpeechTiming? timing, string? text, double? confidence,
        string? detected, R expected)
    {
        var result = ForgeTurnQualityGate.Assess("fr-FR", timing, text, confidence, detected);
        Assert.False(result.Scorable); Assert.Equal(expected, result.Reason); Assert.True(result.RetryOffered);
        Assert.False(result.PronunciationAssessable);
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("FR-ca")]
    [InlineData("fr_BE")]
    [InlineData(null)]
    [InlineData("")]
    public void Regional_or_missing_language_hints_do_not_turn_an_accent_into_a_mismatch(string? detected)
    {
        Assert.True(ForgeTurnQualityGate.Assess("fr-FR", Timing(3), "Je suis prêt", .9, detected).Scorable);
    }

    [Fact]
    public void Unknown_confidence_is_allowed_but_reported()
    {
        var result = ForgeTurnQualityGate.Assess("es-ES", Timing(3), "Quiero practicar mi español", null, "es");
        Assert.True(result.Scorable); Assert.False(result.ConfidenceKnown);
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("fr-fr")]
    [InlineData("de-DE")]
    [InlineData("")]
    public void Every_turn_requires_an_explicit_supported_starter_locale(string locale)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ForgeTurnQualityGate.Assess(locale, Timing(3), "Bonjour", .9));
    }

    [Theory]
    [InlineData(-.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    public void Out_of_range_confidence_is_rejected_rather_than_clamped(double confidence)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ForgeTurnQualityGate.Assess("en-GB", Timing(3), "Hello there", confidence));
    }

    [Fact]
    public void Assessment_is_deterministic()
    {
        var timing = Timing(2.5, .4);
        Assert.Equal(ForgeTurnQualityGate.Assess("en-GB", timing, "Let me explain the trade-off", .7, "en-GB"),
            ForgeTurnQualityGate.Assess("en-GB", timing, "Let me explain the trade-off", .7, "en-GB"));
    }

    [Theory]
    [InlineData("l'école est fermée", 3)]
    [InlineData("  un   deux\ttrois\n", 3)]
    [InlineData("— ... !", 0)]
    [InlineData(null, 0)]
    public void Word_count_ignores_punctuation_only_tokens(string? text, int expected) =>
        Assert.Equal(expected, ForgeTurnQualityGate.Words(text));

    [Fact]
    public void Existing_PCM_meter_feeds_the_gate_silence_and_a_tone_are_judged_from_measured_audio()
    {
        const int rate = 24000;
        Assert.Null(SpeechAudioMeter.Analyse(Pcm(rate, 2, 0), rate));
        Assert.Equal(R.NoMeasuredSpeech, ForgeTurnQualityGate.Assess("fr-FR", SpeechAudioMeter.Analyse(Pcm(rate, 2, 0), rate), "Bonjour", .9).Reason);
        var spoken = SpeechAudioMeter.Analyse(Pcm(rate, 2, 6000), rate);
        Assert.NotNull(spoken);
        Assert.True(ForgeTurnQualityGate.Assess("fr-FR", spoken, "Bonjour, je m'appelle Paul", .9).Scorable);
    }

    private static byte[] Pcm(int rate, double seconds, short amplitude)
    {
        var samples = (int)(rate * seconds); var bytes = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), (short)(amplitude * Math.Sin(2 * Math.PI * 220 * i / rate)));
        return bytes;
    }
}
