using Jarvis.Api;
using Jarvis.Api.Voice;
using Jarvis.Orchestrator.Runtime;
using Microsoft.Extensions.Configuration;

namespace Jarvis.Tests;

public sealed class RealtimeTurnDetectionTests
{
    [Theory]
    [InlineData(null, null, "semantic_vad", "auto")]
    [InlineData(" Semantic_VAD ", " low ", "semantic_vad", "low")]
    [InlineData("server_vad", null, "server_vad", "auto")]
    public void Reads_default_and_owner_turn_detection(string? mode, string? eagerness, string expectedMode, string expectedEagerness)
    {
        var settings = RealtimeTurnDetection.Read(Configuration(mode, eagerness));
        Assert.Equal(expectedMode, settings.Mode);
        Assert.Equal(expectedEagerness, settings.Eagerness);
    }

    [Theory]
    [InlineData("voice-activity", null)]
    [InlineData("semantic_vad", "patient")]
    public void Rejects_unknown_turn_detection_settings(string? mode, string? eagerness)
    {
        var error = Assert.Throws<DomainException>(() => RealtimeTurnDetection.Read(Configuration(mode, eagerness)));
        Assert.Equal(503, error.StatusCode);
    }

    private static IConfiguration Configuration(string? mode, string? eagerness) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ConfigKeys.VoiceTurnDetectionMode] = mode,
            [ConfigKeys.VoiceTurnDetectionEagerness] = eagerness
        }).Build();
}
