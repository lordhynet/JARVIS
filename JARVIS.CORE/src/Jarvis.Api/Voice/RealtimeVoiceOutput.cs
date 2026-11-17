using System.Globalization;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Api.Voice;

internal readonly record struct RealtimeVoiceOutput(string Name, decimal Speed)
{
    private static readonly HashSet<string> BuiltInVoices = new(StringComparer.Ordinal)
    {
        "alloy", "ash", "ballad", "coral", "echo", "sage", "shimmer", "verse", "marin", "cedar"
    };

    internal static RealtimeVoiceOutput Read(IConfiguration configuration)
    {
        var name = configuration[ConfigKeys.VoiceName]?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(name)) name = "marin";
        Guard.Require(BuiltInVoices.Contains(name), 503, "Configure a supported realtime voice name.");

        var speed = 1m;
        var configuredSpeed = configuration[ConfigKeys.VoiceSpeed];
        if (!string.IsNullOrWhiteSpace(configuredSpeed))
        {
            Guard.Require(decimal.TryParse(configuredSpeed, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out speed) && speed is >= 0.25m and <= 1.5m, 503,
                "The realtime voice speed must be a number from 0.25 to 1.5.");
        }

        return new(name, speed);
    }
}
