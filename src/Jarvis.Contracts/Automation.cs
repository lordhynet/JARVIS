namespace Jarvis.Contracts;

public enum AgentRole { InterviewCoach, TechnicalTrainer, CommunicationCoach, Research, Review, CareerPlanner, PersonalPlanner }
public enum AgentTaskStatus { Queued, Running, RetryScheduled, Succeeded, Failed, Cancelled, NeedsReconciliation }
public sealed record AgentSourceRef(string Kind, Guid Id, string Version);
public sealed record AgentSchedule(Guid Id, string Title, string Workflow, string TimeZone, string LocalTime,
    bool Enabled, bool AllowCloudProcessing, int Revision, DateTimeOffset CreatedAt, string? LastOccurrence = null);
public sealed record SaveAgentScheduleRequest(Guid RequestId, int? ExpectedRevision, string Title, string Workflow,
    string TimeZone, string LocalTime, bool Enabled, bool AllowCloudProcessing);
public sealed record WorkflowPolicySnapshot(string Revision, bool RequiresCloud, bool UsesPersonalContext, bool Deterministic, string? DomainContext);
public sealed record AgentRun(Guid Id, Guid? ScheduleId, string Occurrence, string Workflow, int Version, bool AllowCloudProcessing,
    DateTimeOffset CreatedAt, DateTimeOffset Deadline, string Status, IReadOnlyList<Guid> TaskIds, DateTimeOffset? DeletedAt = null,
    WorkflowPolicySnapshot? WorkflowPolicy = null);
public sealed record AgentTask(Guid Id, Guid RunId, Guid? ParentId, AgentRole Role, int SchemaVersion, string Input,
    AgentTaskStatus Status, int Attempt, DateTimeOffset AvailableAt, DateTimeOffset? LeaseExpiresAt = null, Guid? ClaimToken = null,
    string? Output = null, string? ErrorCode = null, DateTimeOffset? CompletedAt = null, IReadOnlyList<AgentSourceRef>? SourceRefs = null);
public sealed record AgentTaskView(Guid Id, Guid RunId, AgentRole Role, AgentTaskStatus Status, int Attempt, string? Output, string? ErrorCode);
public sealed record AgentRunView(AgentRun Run, IReadOnlyList<AgentTaskView> Tasks);
public sealed record StartAgentRunRequest(Guid RequestId, string Workflow, bool AllowCloudProcessing, string? Brief = null);
public sealed record AgentNotification(Guid Id, Guid RunId, string Title, DateTimeOffset CreatedAt, DateTimeOffset DeliverAfter,
    DateTimeOffset ExpiresAt, bool Delivered = false, bool Read = false);
public sealed record AgentControls(bool Paused = false, IReadOnlyList<string>? PausedWorkflows = null, string QuietStart = "22:00", string QuietEnd = "08:00", string TimeZone = "Europe/London");
