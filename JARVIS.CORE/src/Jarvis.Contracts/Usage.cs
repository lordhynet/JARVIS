namespace Jarvis.Contracts;

public enum UsageLane { Background, Interactive, Voice }
public enum VoiceMode { Everyday, Serious }

public sealed record UsageReservation(Guid Id, Guid RunId, string Model, DateTimeOffset At,
    decimal ReservedUsd, decimal? ActualUsd = null, int? InputTokens = null, int? OutputTokens = null,
    bool ReconciliationRequired = false, UsageLane Lane = UsageLane.Background, string Provider = ProviderIds.Anthropic,
    VoiceMode? VoiceMode = null);
public sealed record VoiceUsageEntry(VoiceMode Mode, string Model, DateTimeOffset At, decimal ReservedGbp, decimal? AccountedGbp, string? Outcome);
public sealed record VoiceModeEstimate(VoiceMode Mode, bool PricingReady, string? Model, decimal? MaximumGbpForTenMinutes, string Detail,
    decimal? DailyCapGbp = null);
public sealed record VoiceModeOptions(VoiceMode Recommended, IReadOnlyList<VoiceModeEstimate> Modes);
public sealed record PaidAiStatus(bool Ready, string Detail, decimal DailyLimitUsd, decimal MonthlyLimitUsd,
    decimal BackgroundLimitUsd, decimal TodayCommittedUsd, decimal MonthCommittedUsd, int UncertainCharges);
