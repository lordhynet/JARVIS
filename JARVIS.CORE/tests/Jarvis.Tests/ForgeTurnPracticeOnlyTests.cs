using Jarvis.Orchestrator.Forge;
using R = Jarvis.Orchestrator.Forge.ForgeTurnUnscorableReason;

namespace Jarvis.Tests;

// Owner decision 27 Nov 2026: Forge keeps gpt-transcribe, which supplies no quality evidence. Clean turns are practice only;
// problems a retry can fix still ask for a retry; nothing is ever scored without explicit evidence.
public sealed class ForgeTurnPracticeOnlyTests
{
    private static readonly ForgeSpeechActivity Clean = new(4, 3, ForgeSpeechActivity.PcmActiveMethod);

    private static ForgeTurnQuality NoEvidence(ForgeSpeechActivity? activity, string text, string? detected = "fr") =>
        ForgeTurnQualityGate.Assess("fr-FR", activity, text, null, detected, qualityEvidenceAvailable: false);

    [Fact]
    public void Clean_turn_without_available_evidence_is_practice_only_with_no_retry_and_no_score()
    {
        var result = NoEvidence(Clean, "Je voudrais une table pour deux");
        Assert.True(result.PracticeOnly); Assert.False(result.Scorable); Assert.False(result.RetryOffered);
        Assert.False(result.PronunciationAssessable);
        Assert.Equal("forge-turn-quality-v3", result.RuleVersion);
    }

    public static TheoryData<ForgeSpeechActivity?, string, string?, R> RetryFixable => new()
    {
        { null, "Bonjour", "fr", R.NoMeasuredSpeech },
        { new(4, .5, ForgeSpeechActivity.PcmActiveMethod), "Bonjour", "fr", R.TooShort },
        { new(4, 3, "core-pcm-quiet-v1"), "Bonjour", "fr", R.InvalidTiming },
        { Clean, "  ", "fr", R.NoTranscript },
        { new(2, 1, ForgeSpeechActivity.PcmActiveMethod), "un deux trois quatre cinq six sept huit", "fr", R.ImplausibleTranscript },
        { Clean, "I would like a table for two", "en", R.LanguageMismatch }
    };

    [Theory]
    [MemberData(nameof(RetryFixable))]
    public void Problems_a_retry_can_fix_still_offer_a_retry_before_practice_only(ForgeSpeechActivity? activity, string text, string? detected, R expected)
    {
        var result = NoEvidence(activity, text, detected);
        Assert.Equal(expected, result.Reason); Assert.True(result.RetryOffered); Assert.False(result.PracticeOnly);
    }

    [Fact]
    public void A_model_that_should_supply_evidence_but_did_not_still_asks_for_a_retry()
    {
        var result = ForgeTurnQualityGate.Assess("fr-FR", Clean, "Je voudrais une table", null, "fr");
        Assert.Equal(R.QualityEvidenceMissing, result.Reason); Assert.True(result.RetryOffered); Assert.False(result.PracticeOnly);
    }

    [Fact]
    public void Confidence_from_a_model_declared_without_evidence_is_contradictory_and_rejected() =>
        Assert.Throws<ArgumentException>(() => ForgeTurnQualityGate.Assess("fr-FR", Clean, "Bonjour à tous", .9, "fr", qualityEvidenceAvailable: false));

    [Fact]
    public void Evidence_capable_path_is_unchanged_and_can_still_score() =>
        Assert.True(ForgeTurnQualityGate.Assess("fr-FR", Clean, "Je voudrais une table", .9, "fr").Scorable);
}
