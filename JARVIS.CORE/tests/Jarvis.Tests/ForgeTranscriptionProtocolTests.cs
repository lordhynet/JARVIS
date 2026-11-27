using System.Text.Json;
using Jarvis.Api.Integrations;
using Jarvis.Orchestrator.Forge;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Tests;

// Fixture JSON only, shaped from the documented response. No provider is called.
public sealed class ForgeTranscriptionProtocolTests
{
    private const string Supported = "gpt-4o-transcribe";
    private static ForgeTurnTranscript Parse(string json, string model = Supported) =>
        ForgeTranscriptionProtocol.Parse(JsonDocument.Parse(json).RootElement, model);

    [Fact]
    public void Configured_gpt_transcribe_sends_languages_and_never_yields_confidence()
    {
        Assert.Equal([new("languages[]", "fr")], ForgeTranscriptionProtocol.Fields("gpt-transcribe", "fr-FR"));
        Assert.False(ForgeTranscriptionProtocol.ReturnsConfidence("gpt-transcribe"));
        var transcript = Parse("""{"text":"Bonjour à tous","languages":[{"code":"fr"}],"usage":{"type":"duration","seconds":2.1}}""", "gpt-transcribe");
        Assert.Equal("Bonjour à tous", transcript.Text); Assert.Equal("fr", transcript.DetectedLanguage);
        Assert.Null(transcript.Confidence); Assert.Equal(ForgeTranscriptionProtocol.NoConfidenceMethod, transcript.ConfidenceMethod);
        // Therefore the gate refuses: no explicit quality evidence on the configured model.
        var gate = ForgeTurnQualityGate.Assess("fr-FR", new(3, 2, ForgeSpeechActivity.PcmActiveMethod), transcript.Text, transcript.Confidence, transcript.DetectedLanguage);
        Assert.Equal(ForgeTurnUnscorableReason.QualityEvidenceMissing, gate.Reason);
    }

    [Theory]
    [InlineData("gpt-4o-transcribe")]
    [InlineData("gpt-4o-mini-transcribe")]
    public void Logprob_models_request_json_logprobs_with_the_profile_language(string model)
    {
        Assert.Equal([new("language", "es"), new("response_format", "json"), new("include[]", "logprobs")],
            ForgeTranscriptionProtocol.Fields(model, "es-ES"));
        Assert.True(ForgeTranscriptionProtocol.ReturnsConfidence(model));
    }

    [Fact]
    public void Other_models_get_a_language_hint_only() =>
        Assert.Equal([new("language", "en")], ForgeTranscriptionProtocol.Fields("whisper-1", "en-GB"));

    [Fact]
    public void Fields_require_a_starter_locale_and_a_model()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ForgeTranscriptionProtocol.Fields("gpt-transcribe", "fr"));
        Assert.Equal(503, Assert.Throws<DomainException>(() => ForgeTranscriptionProtocol.Fields(" ", "fr-FR")).StatusCode);
    }

    [Fact]
    public void Confidence_is_the_geometric_mean_token_probability_and_feeds_the_gate()
    {
        var transcript = Parse("""{"text":"Je suis prêt","logprobs":[{"token":"Je","logprob":-0.1,"bytes":[74,101]},{"token":" suis","logprob":-0.3},{"token":" prêt","logprob":-0.2}]}""");
        Assert.Equal(Math.Exp(-0.2), transcript.Confidence!.Value, 9);
        Assert.Equal(ForgeTranscriptionProtocol.LogprobConfidenceMethod, transcript.ConfidenceMethod);
        Assert.Null(transcript.DetectedLanguage);
        Assert.True(ForgeTurnQualityGate.Assess("fr-FR", new(3, 2, ForgeSpeechActivity.PcmActiveMethod), transcript.Text, transcript.Confidence).Scorable);
    }

    [Theory]
    [InlineData("""{"text":"x","logprobs":[]}""")]
    [InlineData("""{"text":"x","logprobs":null}""")]
    [InlineData("""{"text":"x","logprobs":[{"token":"x"}]}""")]
    [InlineData("""{"text":"x","logprobs":[{"logprob":-0.1},{"logprob":"-0.2"}]}""")]
    [InlineData("""{"text":"x","logprobs":[{"logprob":-0.1},{"logprob":0.5}]}""")]
    [InlineData("""{"text":"x","logprobs":[-0.1]}""")]
    public void Missing_or_malformed_logprobs_void_confidence_rather_than_repairing_it(string json) =>
        Assert.Null(Parse(json).Confidence);

    [Theory]
    [InlineData("""{"text":"x","languages":[{"code":"fr"},{"code":"en"}]}""")]
    [InlineData("""{"text":"x","languages":[]}""")]
    [InlineData("""{"text":"x","languages":[{"code":7}]}""")]
    [InlineData("""{"text":"x","languages":["fr"]}""")]
    [InlineData("""{"text":"x"}""")]
    public void Only_a_single_well_formed_detected_language_is_reported(string json) =>
        Assert.Null(Parse(json).DetectedLanguage);

    [Theory]
    [InlineData("""{"languages":[{"code":"fr"}]}""")]
    [InlineData("""{"text":5}""")]
    [InlineData("""[]""")]
    public void Missing_text_is_a_provider_error(string json) =>
        Assert.Equal(502, Assert.Throws<DomainException>(() => Parse(json)).StatusCode);

    // ---- Review T1: evidence admission is bound to the reviewed model capability ----

    private const string UnexpectedLogprobs =
        """{"text":"Bonjour je voudrais parler","languages":[{"code":"fr"}],"logprobs":[{"token":"Bonjour","logprob":-0.01}]}""";

    [Theory]
    [InlineData("gpt-transcribe")]
    [InlineData("whisper-1")]
    [InlineData("unreviewed-future-model")]
    public void Unexpected_logprobs_from_a_model_without_reviewed_evidence_are_never_admitted(string model)
    {
        var transcript = Parse(UnexpectedLogprobs, model);
        Assert.Null(transcript.Confidence); Assert.Equal(ForgeTranscriptionProtocol.NoConfidenceMethod, transcript.ConfidenceMethod);
        Assert.Equal(model, transcript.Model); Assert.False(transcript.QualityEvidenceAvailable);
        var gate = ForgeTurnQualityGate.Assess("fr-FR", new(3, 2, ForgeSpeechActivity.PcmActiveMethod), transcript.Text, transcript.Confidence, transcript.DetectedLanguage);
        Assert.False(gate.Scorable);
    }

    [Fact]
    public void Supported_model_derives_confidence_from_the_same_response_positive_control()
    {
        var transcript = Parse(UnexpectedLogprobs, Supported);
        Assert.Equal(Math.Exp(-0.01), transcript.Confidence!.Value, 9); Assert.True(transcript.QualityEvidenceAvailable);
        Assert.True(ForgeTurnQualityGate.Assess("fr-FR", new(3, 2, ForgeSpeechActivity.PcmActiveMethod), transcript.Text, transcript.Confidence, transcript.DetectedLanguage).Scorable);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void Parse_requires_the_reviewed_model(string model) =>
        Assert.Equal(503, Assert.Throws<DomainException>(() => Parse(UnexpectedLogprobs, model)).StatusCode);

    // ---- Review T2: malformed language strings are unknown, never a mismatch ----

    [Theory]
    [InlineData("  ")]
    [InlineData("??")]
    [InlineData("fr!!!")]
    [InlineData("f")]
    [InlineData("english")]
    [InlineData("fr-")]
    [InlineData("-fr")]
    [InlineData("fr-ca-x")]
    [InlineData("f1")]
    [InlineData("fr\u0000")]
    [InlineData("fr_ca")]
    public void Malformed_detected_language_stays_unknown_and_cannot_cause_a_false_mismatch(string code)
    {
        var json = JsonSerializer.Serialize(new { text = "Je suis prêt", languages = new[] { new { code } },
            logprobs = new[] { new { logprob = -0.1 } } });
        var transcript = Parse(json);
        Assert.Null(transcript.DetectedLanguage);
        Assert.True(ForgeTurnQualityGate.Assess("fr-FR", new(3, 2, ForgeSpeechActivity.PcmActiveMethod), transcript.Text, transcript.Confidence, transcript.DetectedLanguage).Scorable);
    }

    [Theory]
    [InlineData("fr", "fr", true)]
    [InlineData("FR", "FR", true)]
    [InlineData("fra", "fr", true)]
    [InlineData("fre", "fr", true)]
    [InlineData("fr-ca", "fr-ca", true)]
    [InlineData("en", "en", false)]
    [InlineData("eng", "en", false)]
    [InlineData("deu", "deu", false)]
    [InlineData("zh-cn", "zh-cn", false)]
    public void Well_formed_codes_are_reported_and_genuine_other_languages_remain_mismatches(string code, string reported, bool scorable)
    {
        var json = JsonSerializer.Serialize(new { text = "Je suis prêt", languages = new[] { new { code } },
            logprobs = new[] { new { logprob = -0.1 } } });
        var transcript = Parse(json);
        Assert.Equal(reported, transcript.DetectedLanguage);
        var gate = ForgeTurnQualityGate.Assess("fr-FR", new(3, 2, ForgeSpeechActivity.PcmActiveMethod), transcript.Text, transcript.Confidence, transcript.DetectedLanguage);
        Assert.Equal(scorable, gate.Scorable);
        if (!scorable) Assert.Equal(ForgeTurnUnscorableReason.LanguageMismatch, gate.Reason);
    }
}
