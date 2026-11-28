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
    // Sized for the tool set the window actually sends: the flag-gated generated-practice tool is counted only when offered,
    // so enabling it raises the reservation and leaving it off leaves the reservation unchanged.
    private static readonly int BaseToolBytes = JsonSerializer.SerializeToUtf8Bytes(CoreVoiceTools.DefinitionsFor(false)).Length;
    private static readonly int GenerationToolBytes = JsonSerializer.SerializeToUtf8Bytes(CoreVoiceTools.DefinitionsFor(false, generation: true)).Length;
    internal static int ToolBytesFor(bool generation) => generation ? GenerationToolBytes : BaseToolBytes;

    internal static decimal Maximum(VoiceWindowLimits window, RealtimePricing pricing, int promptBytes, bool generation = false)
    {
        var inputBound = promptBytes + ToolBytesFor(generation) + ConversationTokenLimit + AudioPrefixOverhead;
        return (inputBound * window.MaxTurns * pricing.MaxInput + window.MaxTurns * OutputTokenLimit * pricing.MaxOutput)
            / 1_000_000m + pricing.Transcription(window.Seconds);
    }

    internal static (decimal MaximumPerWindowUsd, int Windows) EstimateTenMinutes(VoiceWindowLimits window, RealtimePricing pricing, string persona,
        bool generation = false)
    {
        var instructionBytes = Encoding.UTF8.GetByteCount(RealtimeVoicePrompt.Build(persona, "", "", continuing: true));
        var promptBound = instructionBytes + VoiceConversationMemory.RecentByteLimit + VoiceConversationMemory.SummaryByteLimit
            + CurrentTaskByteLimit + PracticeContextByteLimit + SessionJsonEnvelopeBytes + PreparedPromptAllowanceBytes;
        return (Maximum(window, pricing, promptBound, generation), WindowsInTenMinutes(window));
    }

    internal static int WindowsInTenMinutes(VoiceWindowLimits window) => (EstimateSeconds + window.Seconds - 1) / window.Seconds;
}
