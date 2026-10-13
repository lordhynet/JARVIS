namespace Jarvis.Contracts;

public enum LearningDomain
{
    LeetCode,
    SystemDesign,
    French,
    InterviewStory,
    Communication
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
    DateTimeOffset CreatedAt);

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
    DateTimeOffset ReviewedAt);
