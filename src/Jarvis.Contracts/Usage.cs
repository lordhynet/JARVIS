namespace Jarvis.Contracts;

public enum UsageLane { Background, Interactive, Voice }

public sealed record UsageReservation(Guid Id, Guid RunId, string Model, DateTimeOffset At,
    decimal ReservedUsd, decimal? ActualUsd = null, int? InputTokens = null, int? OutputTokens = null,
    bool ReconciliationRequired = false, UsageLane Lane = UsageLane.Background, string Provider = ProviderIds.Anthropic);
public sealed record PaidAiStatus(bool Ready, string Detail, decimal DailyLimitUsd, decimal MonthlyLimitUsd,
    decimal BackgroundLimitUsd, decimal TodayCommittedUsd, decimal MonthCommittedUsd, int UncertainCharges);
