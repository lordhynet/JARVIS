namespace Jarvis.Contracts;

public enum CoachingArea { Communication, Interview }
public sealed record VoiceCoaching(Guid Id, Guid SessionId, string ExerciseId, Guid? StoryId,
    int? StoryVersion, int? ClaimIndex, Guid StartUtteranceId, DateTimeOffset CreatedAt,
    string Status = "Question", Guid? AnswerUtteranceId = null, Guid? AttemptId = null, Guid? StopUtteranceId = null, int ExerciseVersion = 1,
    Guid? CorrectionStartUtteranceId = null, int? CorrectionExpectedRevision = null, DateTimeOffset? CorrectionStartedAt = null,
    DateTimeOffset? UpdatedAt = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? ScenarioKey = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Guid? ScenarioVersionId = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] int ScenarioStep = 0,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<Guid>? ScenarioAttempts = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<Guid>? ScenarioAnswers = null);
public sealed record CoachingExercise(string Id, int Version, CoachingArea Area, string Title, string Prompt,
    IReadOnlyList<string> Framework, IReadOnlyList<string> FollowUps, int? TargetSeconds = null, bool RequiresStory = false, bool RequiresClaim = false);
public sealed record CoachingPrompt(CoachingExercise Exercise, string Question, Guid? StoryId, int? StoryVersion, int? ClaimIndex);
public sealed record SaveCoachingAnswerRequest(Guid RequestId, Guid SessionId, string ExerciseId, int ExerciseVersion,
    string Text, Guid? StoryId = null, int? StoryVersion = null, int? ClaimIndex = null, double? ReportedDurationSeconds = null);
public sealed record CorrectCoachingAnswerRequest(Guid RequestId, int ExpectedRevision, string Text, double? ReportedDurationSeconds = null);
public sealed record CoachingAnswerVersion(int Version, string Text, double? ReportedDurationSeconds, DateTimeOffset At);
public sealed record CoachingAttempt(Guid Id, Guid SessionId, string ExerciseId, int ExerciseVersion, Guid? StoryId,
    int? StoryVersion, int? ClaimIndex, int Revision, DateTimeOffset CreatedAt, IReadOnlyList<CoachingAnswerVersion> Answers, DateTimeOffset? DeletedAt = null, Guid? AiAssessmentId = null);
public sealed record TextObservation(string Code, string Detail, IReadOnlyList<string> Evidence);
public sealed record CoachingAnalysis(int WordCount, int SentenceCount, int FillerCandidates, int FirstPersonSingularCount,
    int FirstPersonPluralCount, double? ReportedDurationSeconds, double? ApproximateWordsPerMinute,
    string TimingSource, IReadOnlyList<TextObservation> Observations, IReadOnlyList<string> NextDrills, string RuleVersion);
public sealed record CoachingAttemptView(CoachingAttempt Attempt, CoachingExercise Exercise, CoachingAnalysis Analysis, bool SourceCurrent, AiCoachAssessment? AiAssessment = null);
public sealed record CoachingAttemptSummary(Guid Id, Guid SessionId, string ExerciseId, string Title, int Revision, DateTimeOffset CreatedAt, int WordCount, bool SourceCurrent);
public sealed record ProgrammeWeek(int Number, string Focus, IReadOnlyList<string> Tasks);
public sealed record StartProgrammeRequest(Guid RequestId, string LocalStartDate, string TimeZone = "Europe/London");
public sealed record ProgrammeCheckInRequest(Guid RequestId, int ExpectedRevision, int Week, string? Note);
public sealed record ProgrammeCheckIn(Guid Id, int Week, string Note, DateTimeOffset At);
public sealed record ImprovementProgramme(Guid Id, string LocalStartDate, string TimeZone, int Revision, DateTimeOffset CreatedAt, IReadOnlyList<ProgrammeCheckIn> CheckIns, DateTimeOffset? DeletedAt = null);
public sealed record ProgrammeView(ImprovementProgramme? Programme, int CurrentWeek, IReadOnlyList<ProgrammeWeek> Weeks, string ProgressSource);
public sealed record ProductModule(string Id, string Title, string Status, IReadOnlyList<string> Available, IReadOnlyList<string> Remaining);
public sealed record SessionReview(Guid SessionId, string Title, string Fingerprint, int TechnicalAttempts, int SkippedQuestions,
    int CoachingAttempts, int UserMessages, IReadOnlyList<string> Questions, IReadOnlyList<WeakDomain> SelfRatedDomains,
    IReadOnlyList<TextObservation> TextObservations, IReadOnlyList<string> NextDrills, string AssessmentSource);
