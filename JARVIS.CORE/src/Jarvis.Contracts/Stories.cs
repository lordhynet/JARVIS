namespace Jarvis.Contracts;

// Drafts may be incomplete: coaching must elicit real details rather than invent them.
public sealed record StoryContent(
    string Title, string CompanyProject, string ServiceFeature,
    string Context = "", string Problem = "", string Constraints = "",
    string PersonalOwnership = "", string TechnicalDecisions = "", string TradeOffs = "",
    string Implementation = "", string Collaboration = "", string FailureModes = "",
    string Result = "", string Metrics = "", string ChangeToday = "",
    string[]? FollowUpQuestions = null, string[]? CvClaims = null);

public sealed record Story(Guid Id, StoryContent Content, int Version,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record CreateStoryRequest(Guid RequestId, StoryContent Content);
public sealed record UpdateStoryRequest(int ExpectedVersion, StoryContent Content);
/// <summary>One deterministic interview-readiness check. Status is "ready", "weak" or "missing".</summary>
public sealed record StoryReadinessCheck(string Key, string Label, string Status, string Advice);
/// <summary>
/// How interview-ready a Story Bank story is, from its own fields and attached source quotes. A text checklist, not a
/// judgement of whether the story is true; it never suggests inventing facts or measurements.
/// </summary>
public sealed record StoryReadiness(Guid StoryId, int Version, string Title, int Score, string Level,
    IReadOnlyList<StoryReadinessCheck> Checks, IReadOnlyList<string> FollowUps, int SourceQuotes);
