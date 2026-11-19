using System.Text;
using System.Text.Json;
using Jarvis.Api.Voice;
using Jarvis.Orchestrator.Intelligence;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Api.Integrations;

internal static class RealtimeWindowCost
{
    internal const int OutputTokenLimit = 256;
    internal const int ConversationTokenLimit = 4096;
    internal const int PreparedPromptAllowanceBytes = 1800;
    internal const int CurrentTaskByteLimit = 500;
    internal const int PracticeContextByteLimit = 5000;
    private const int AudioPrefixOverhead = 5120;
    private const int SessionJsonEnvelopeBytes = 100;
    private const int EstimateSeconds = 600;
    private static readonly int ToolBytes = JsonSerializer.SerializeToUtf8Bytes(CoreVoiceTools.Definitions).Length;

    internal static decimal Maximum(VoiceWindowLimits window, RealtimePricing pricing, int promptBytes)
    {
        var inputBound = promptBytes + ToolBytes + ConversationTokenLimit + AudioPrefixOverhead;
        return (inputBound * window.MaxTurns * pricing.MaxInput + window.MaxTurns * OutputTokenLimit * pricing.MaxOutput)
            / 1_000_000m + pricing.Transcription(window.Seconds);
    }

    internal static (decimal MaximumPerWindowUsd, int Windows) EstimateTenMinutes(VoiceWindowLimits window, RealtimePricing pricing, string persona)
    {
        var instructionBytes = Encoding.UTF8.GetByteCount(RealtimeVoicePrompt.Build(persona, "", "", continuing: true));
        var promptBound = instructionBytes + VoiceConversationMemory.RecentByteLimit + VoiceConversationMemory.SummaryByteLimit
            + CurrentTaskByteLimit + PracticeContextByteLimit + SessionJsonEnvelopeBytes + PreparedPromptAllowanceBytes;
        return (Maximum(window, pricing, promptBound), WindowsInTenMinutes(window));
    }

    internal static int WindowsInTenMinutes(VoiceWindowLimits window) => (EstimateSeconds + window.Seconds - 1) / window.Seconds;
}
