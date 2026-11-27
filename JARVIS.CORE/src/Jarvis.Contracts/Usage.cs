using System.Text.Json.Serialization;

namespace Jarvis.Contracts;

public enum UsageLane { Background, Interactive, Voice }
public enum VoiceMode { Everyday, Serious }

// Optional programme attribution inside the single global AI budget. It never creates a separate ledger or limit
// that could bypass the global ceiling; it only narrows what an attributed caller may reserve.
public static class UsageProgrammes
{
    public const string Forge = "forge";
    public const string ForgeAllowanceReached = "forge_allowance";
}

// Programme is omitted when null so existing runtime documents and exports serialize exactly as before.
public sealed record UsageReservation(Guid Id, Guid RunId, string Model, DateTimeOffset At,
    decimal ReservedUsd, decimal? ActualUsd = null, int? InputTokens = null, int? OutputTokens = null,
    bool ReconciliationRequired = false, UsageLane Lane = UsageLane.Background, string Provider = ProviderIds.Anthropic,
    VoiceMode? VoiceMode = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Programme = null);
public sealed record VoiceUsageEntry(VoiceMode Mode, string Model, DateTimeOffset At, decimal ReservedGbp, decimal? AccountedGbp, string? Outcome);
public sealed record VoiceModeEstimate(VoiceMode Mode, bool PricingReady, string? Model, decimal? MaximumGbpForTenMinutes, string Detail,
    decimal? DailyCapGbp = null);
public sealed record VoiceModeOptions(VoiceMode Recommended, IReadOnlyList<VoiceModeEstimate> Modes);
public sealed record PaidAiStatus(bool Ready, string Detail, decimal DailyLimitUsd, decimal MonthlyLimitUsd,
    decimal BackgroundLimitUsd, decimal TodayCommittedUsd, decimal MonthCommittedUsd, int UncertainCharges);
