namespace Jarvis.Contracts;

public sealed record PersonalGoal(Guid Id, int Revision, string Title, string Brief, string Workflow,
    bool AllowCloudProcessing, DateTimeOffset NextReviewAt, DateTimeOffset Deadline, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, Guid? LastRunId = null);
public sealed record SaveGoalRequest(Guid RequestId, int? ExpectedRevision, string Title, string Brief,
    string Workflow, bool AllowCloudProcessing, DateTimeOffset NextReviewAt, DateTimeOffset Deadline, string Status = GoalStatus.Active);
public sealed record AgentTriggerRequest(string Source, string OccurrenceId, long Revision, string Workflow,
    DateTimeOffset DueAt, DateTimeOffset ExpiresAt, string Brief, bool AllowCloudProcessing,
    string CorrelationId, bool Cancelled = false);
public sealed record DurableAgentTrigger(Guid Id, string Source, string OccurrenceId, long Revision,
    string Workflow, DateTimeOffset DueAt, DateTimeOffset ExpiresAt, string Brief, bool AllowCloudProcessing,
    string CorrelationId, string Fingerprint, string Status, Guid? RunId = null);
public sealed record ReconcileUsageRequest(Guid RequestId, decimal ActualUsd, string Evidence, string Confirmation, decimal? ActualGbp = null);
public sealed record UsageReconciliation(Guid Id, Guid ReservationId, decimal ActualUsd, string Evidence, DateTimeOffset At, decimal? ActualGbp = null);
