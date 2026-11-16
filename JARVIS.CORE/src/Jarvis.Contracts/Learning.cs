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
    string SchedulingRuleVersion = "manual-interval-v1", string MasterySource = "unknown");

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
