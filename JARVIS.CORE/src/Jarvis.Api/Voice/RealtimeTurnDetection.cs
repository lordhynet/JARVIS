using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Api.Voice;

internal readonly record struct RealtimeTurnDetection(string Mode, string Eagerness)
{
    internal const string Semantic = "semantic_vad";
    internal const string Server = "server_vad";

    internal bool IsSemantic => Mode == Semantic;

    internal static RealtimeTurnDetection Read(IConfiguration configuration)
    {
        var mode = configuration[ConfigKeys.VoiceTurnDetectionMode]?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(mode)) mode = Semantic;
        Guard.Require(mode is Semantic or Server, 503, "The realtime turn detection mode must be semantic_vad or server_vad.");

        var eagerness = configuration[ConfigKeys.VoiceTurnDetectionEagerness]?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(eagerness)) eagerness = "auto";
        Guard.Require(eagerness is "auto" or "low" or "medium" or "high", 503, "The semantic VAD eagerness must be auto, low, medium or high.");
        return new(mode, eagerness);
    }

    internal object ToProviderSettings() => IsSemantic
        ? new { type = Mode, eagerness = Eagerness, create_response = false, interrupt_response = true }
        : new { type = Mode, threshold = .5, prefix_padding_ms = 300, silence_duration_ms = 650, create_response = false, interrupt_response = true };
}
