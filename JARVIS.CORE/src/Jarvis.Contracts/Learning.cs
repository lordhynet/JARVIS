namespace Jarvis.Contracts;

public enum LearningDomain
{
    LeetCode,
    SystemDesign,
    French,
    InterviewStory,
    Communication,
    CSharpDotNet,
    DistributedSystems,
    Databases,
    ApiSecurity,
    ProductionDevOps,
    PaymentsFintech
}

/// <summary>One thing to remember, scheduled by the spaced-repetition engine.</summary>
public sealed record LearningItem(
    Guid Id,
    LearningDomain Domain,
    string Prompt,
    string Answer,
    double Stability,
    double Difficulty,
    DateTimeOffset DueAt,
    DateTimeOffset CreatedAt,
    double? MasteryScore = null,
    int StrongStreak = 0,
    string SchedulingRuleVersion = "manual-interval-v1", string MasterySource = "unknown",
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Guid? SourceRecordingId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Guid? RecordedQuestionId = null);

/// <summary>How well a review went, on the standard 4-point scale used by FSRS/Anki.</summary>
public enum ReviewRating
{
    Again = 1,
    Hard = 2,
    Good = 3,
    Easy = 4
}

public sealed record ReviewEvent(
    Guid Id,
    Guid LearningItemId,
    ReviewRating Rating,
    TimeSpan ResponseTime,
    DateTimeOffset ReviewedAt, string TimingSource = "user_reported");

// Stores references only; captured words remain in the canonical session/voice history.
public sealed record VoiceRecall(Guid Id, Guid SessionId, Guid CardId, string CardFingerprint, string Status,
    Guid StartUtteranceId, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    Guid? AnswerUtteranceId = null, Guid? RatingUtteranceId = null, ReviewRating? Rating = null);

/// <summary>Per-domain learning load: what is due and how the last seven days of reviews went.</summary>
public sealed record LearningDomainOverview(LearningDomain Domain, int Cards, int DueNow, int DueNext7Days,
    int Reviewed7Days, int Again7Days, double? RecallRate7Days);
/// <summary>
/// Deterministic learning overview. Forecast[0] is everything due within 24 hours (overdue included); Forecast[1..6] are
/// the following 24-hour windows. RecallRate is the share of last-7-day reviews not rated Again; null when none.
/// </summary>
public sealed record LearningOverview(DateTimeOffset AsOf, int Cards, int DueNow, IReadOnlyList<int> Forecast,
    int Reviewed7Days, int Again7Days, int Hard7Days, int Good7Days, int Easy7Days, double? RecallRate7Days,
    LearningDomain? Focus, IReadOnlyList<LearningDomainOverview> Domains);
