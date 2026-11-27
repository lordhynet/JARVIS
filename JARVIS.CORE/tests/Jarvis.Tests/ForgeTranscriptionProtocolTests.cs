using System.Text.Json;
using Jarvis.Api.Integrations;
using Jarvis.Orchestrator.Forge;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Tests;

// Fixture JSON only, shaped from the documented response. No provider is called.
public sealed class ForgeTranscriptionProtocolTests
{
    private static ForgeTurnTranscript Parse(string json) => ForgeTranscriptionProtocol.Parse(JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Configured_gpt_transcribe_sends_languages_and_never_yields_confidence()
    {
        Assert.Equal([new("languages[]", "fr")], ForgeTranscriptionProtocol.Fields("gpt-transcribe", "fr-FR"));
        Assert.False(ForgeTranscriptionProtocol.ReturnsConfidence("gpt-transcribe"));
        var transcript = Parse("""{"text":"Bonjour à tous","languages":[{"code":"fr"}],"usage":{"type":"duration","seconds":2.1}}""");
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
}
