namespace Jarvis.Contracts;

// Existing numeric values are retained for persisted runtime documents.
public enum TrainingMode { RapidDrill, ExplainSimply, SpacedReview, DeepDive, ArchitectureChallenge, FailureInjection, ProductionIncident, CodeReview, RandomMixedInterview }
public sealed record DrillModeInfo(TrainingMode Mode, string Title, string Description);
// Topic is optional and omitted when null, so legacy requests, drills and replays keep their exact JSON shape and meaning.
public sealed record StartTrainingDrillRequest(Guid RequestId, Guid SessionId, TrainingMode Mode, Guid? QuestionId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] LearningDomain? Topic = null);
public sealed record CancelTrainingDrillRequest(int ExpectedRevision);
public sealed record TrainingDrillStep(int Index, Guid QuestionId, int QuestionVersion, string Title, string Prompt,
    string Reference, string SourceUrl, string? CodeSnippet = null, int? SuggestedSeconds = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Guid? GeneratedQuestionId = null);
public sealed record TrainingDrill(Guid Id, Guid SessionId, TrainingMode Mode, Guid? RequestedQuestionId, int Revision,
    string Status, DateTimeOffset CreatedAt, IReadOnlyList<TrainingDrillStep> Steps, IReadOnlyList<Guid> AttemptIds,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] LearningDomain? RequestedTopic = null,
    // Request IDs that successfully resumed this drill, with their input fingerprint, so a replay stays bound here.
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyDictionary<Guid, string>? ResumedBy = null);
public sealed record TrainingDrillView(TrainingDrill Drill, IReadOnlyList<TrainingAttempt> Answers);
public enum AssessmentDimension { TechnicalAccuracy, Specificity, PersonalOwnership, ArchitectureReasoning, TradeOffs, CommunicationStructure, RecallStability }
public sealed record TrainingQuestion(Guid Id, int Version, LearningDomain Domain, string Title,
    string Prompt, string Reference, IReadOnlyList<string> FollowUps, string SourceUrl);
public sealed record SubmitTrainingAnswerRequest(Guid RequestId, Guid SessionId, Guid QuestionId,
    int QuestionVersion, TrainingMode Mode, string? Text, bool Skipped = false, Guid? DrillId = null, int? StepIndex = null);
public sealed record DimensionScore(AssessmentDimension Dimension, int? Value, string? Evidence, string? Rationale);
public sealed record SelfAssessmentRequest(Guid RequestId, int ExpectedRevision, int TextRevision,
    IReadOnlyList<DimensionScore>? Scores, string TimeZone = "Europe/London");
public sealed record CorrectTrainingAnswerRequest(Guid RequestId, int ExpectedRevision, string Text);
public sealed record TrainingAnswerVersion(int Revision, string Text, string Source, DateTimeOffset At);
public sealed record TrainingAssessment(Guid Id, int Version, int TextRevision, string Source, string RubricVersion,
    IReadOnlyList<DimensionScore> Scores, double? ContentScore, string TimeZone, DateTimeOffset At);
public sealed record TrainingAttempt(Guid Id, Guid SessionId, Guid QuestionId, int QuestionVersion, TrainingMode Mode,
    long Sequence, int Revision, bool Skipped, DateTimeOffset SubmittedAt,
    IReadOnlyList<TrainingAnswerVersion> Answers, IReadOnlyList<TrainingAssessment> Assessments,
    Guid? CanonicalAssessmentId = null, DateTimeOffset? DeletedAt = null, Guid? DrillId = null, int? StepIndex = null)
{
    public string CurrentAnswer => Answers.LastOrDefault()?.Text ?? "";
    public int TextRevision => Answers.LastOrDefault()?.Revision ?? 0;
    public TrainingAssessment? CanonicalAssessment => Assessments.FirstOrDefault(x => x.Id == CanonicalAssessmentId);
    public double? WordsPerMinute => null; // Text cannot establish audio pace.
    public long? AnswerDurationMs => null;
}
public sealed record TrainingAttemptSummary(Guid Id, Guid SessionId, Guid QuestionId, int Revision,
    bool Skipped, DateTimeOffset SubmittedAt, double? ContentScore, string AssessmentSource);
public sealed record WeakDomain(LearningDomain Domain, double MeanScore, int RatedQuestions, string AssessmentSource = "self");
public sealed record TrainingDashboard(int CompletedAttempts, int SkippedAttempts, int PracticeDays,
    int DueQuestions, IReadOnlyList<WeakDomain> WeakDomains, IReadOnlyList<TrainingAttemptSummary> RecentAttempts,
    string TimeZone);
