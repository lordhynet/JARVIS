using Jarvis.Api;
using Jarvis.Api.Voice;
using Jarvis.Orchestrator.Runtime;
using Microsoft.Extensions.Configuration;

namespace Jarvis.Tests;

public sealed class RealtimeVoiceOutputTests
{
    [Theory]
    [InlineData(null, null, "marin", 1)]
    [InlineData(" Cedar ", "0.25", "cedar", 0.25)]
    [InlineData("ash", "1.5", "ash", 1.5)]
    public void Reads_supported_output_settings(string? name, string? speed, string expectedName, double expectedSpeed)
    {
        var output = RealtimeVoiceOutput.Read(Configuration(name, speed));
        Assert.Equal(expectedName, output.Name);
        Assert.Equal((decimal)expectedSpeed, output.Speed);
    }

    [Theory]
    [InlineData("unsupported", null)]
    [InlineData(null, "0.24")]
    [InlineData(null, "1.51")]
    [InlineData(null, "1,25")]
    [InlineData(null, "NaN")]
    public void Rejects_invalid_output_settings(string? name, string? speed)
    {
        var error = Assert.Throws<DomainException>(() => RealtimeVoiceOutput.Read(Configuration(name, speed)));
        Assert.Equal(503, error.StatusCode);
    }

    private static IConfiguration Configuration(string? name, string? speed) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ConfigKeys.VoiceName] = name,
            [ConfigKeys.VoiceSpeed] = speed
        }).Build();
}
