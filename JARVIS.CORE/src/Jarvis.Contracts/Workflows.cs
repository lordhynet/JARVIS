namespace Jarvis.Contracts;

public enum WorkflowStatus { Queued, Running, RetryScheduled, Succeeded, Failed, Cancelled, Expired }
public sealed record CreatePracticePlanRequest(Guid RequestId, DateTimeOffset? NotBefore = null);
public sealed record CreateInterviewBriefRequest(Guid RequestId, bool AllowCloudProcessing);
public sealed record InterviewBrief(string Summary, IReadOnlyList<string> Drills,
    IReadOnlyList<Guid> SourceIds, string Model, DateTimeOffset GeneratedAt);
public sealed record PracticeStep(string Kind, Guid ResourceId, string Prompt);
public sealed record PracticePlan(IReadOnlyList<PracticeStep> Steps, IReadOnlyList<string> Gaps);
public sealed record WorkflowRun(Guid Id, string Kind, int WorkflowVersion, string Fingerprint,
    WorkflowStatus Status, int Attempt, int Revision, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? AvailableAt,
    Guid? ClaimToken = null, string? WorkerId = null, DateTimeOffset? LeaseExpiresAt = null,
    string? ErrorCode = null, PracticePlan? Result = null, bool AllowCloudProcessing = false,
    InterviewBrief? Brief = null, DateTimeOffset? DeletedAt = null);

// Durable, content-free event. Revision provides ordering and duplicate suppression.
public sealed record WorkflowEvent(Guid EventId, Guid RunId, int Revision,
    WorkflowStatus Status, DateTimeOffset OccurredAt);

// Worker claim tokens and request fingerprints are internal, never returned to clients.
public sealed record WorkflowRunView(Guid Id, string Kind, int WorkflowVersion,
    WorkflowStatus Status, int Attempt, int Revision, DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? AvailableAt,
    string? ErrorCode, PracticePlan? Result, InterviewBrief? Brief)
{
    public static WorkflowRunView From(WorkflowRun run) => new(run.Id, run.Kind, run.WorkflowVersion,
        run.Status, run.Attempt, run.Revision, run.CreatedAt, run.UpdatedAt,
        run.ExpiresAt, run.AvailableAt, run.ErrorCode, run.Result, run.Brief);
}
