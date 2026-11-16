namespace Jarvis.Contracts;

public sealed record PrivacyStatus(long Epoch, int Revision, bool Erased, DateTimeOffset? ErasedAt, Guid? ErasureRequestId);
public sealed record EraseCoreRequest(Guid RequestId, int ExpectedRevision, string Confirmation);
public sealed record ReopenCoreRequest(int ExpectedRevision, string Confirmation);
public sealed record RetentionPolicy(bool Enabled = false, int SessionDays = 180, int AnswerDays = 180, int VoiceDays = 30,
    int ProviderCacheDays = 7, int ArtifactDays = 30, int SweepMinutes = 60);
public sealed record PrivacyCounts(int Sessions, int TrainingAnswers, int CoachingAnswers, int VoiceTurns, int ProviderResults,
    int WorkflowArtifacts, int AgentArtifacts, int Imports = 0, int DeviceGrants = 0, int DeviceCommands = 0);
public sealed record RetentionPreview(DateTimeOffset AsOf, string PolicyFingerprint, RetentionPolicy Policy, PrivacyCounts Eligible);
public sealed record ApplyRetentionRequest(Guid RequestId, DateTimeOffset AsOf, string PolicyFingerprint, string Confirmation);
public sealed record PrivacyJob(Guid Id, string Kind, string Fingerprint, DateTimeOffset CompletedAt, PrivacyCounts Removed);
public sealed record AgentTaskExport(Guid Id, Guid RunId, AgentRole Role, AgentTaskStatus Status, string Input,
    string? Output, string? ErrorCode, IReadOnlyList<AgentSourceRef> SourceRefs);
public sealed record CoreDataExport(int SchemaVersion, DateTimeOffset ExportedAt, PrivacyStatus Privacy,
    IReadOnlyList<ConversationSession> Sessions, IReadOnlyList<Story> Stories, IReadOnlyList<LearningItem> Learning,
    IReadOnlyList<LearningReview> Reviews, IReadOnlyList<TrainingAttempt> Training, IReadOnlyList<TrainingDrill> Drills,
    IReadOnlyList<CoachingAttempt> Coaching, IReadOnlyList<AiCoachAssessment> AiAssessments, IReadOnlyList<ImprovementProgramme> Programmes,
    IReadOnlyList<RegisteredDevice> Devices, IReadOnlyList<VoiceTurn> VoiceTurns, IReadOnlyList<AuditedTool> ToolAudit,
    IReadOnlyList<WorkflowRunView> Workflows, IReadOnlyList<WorkflowEvent> WorkflowEvents, IReadOnlyList<AgentRun> AgentRuns,
    IReadOnlyList<AgentTaskExport> AgentTasks, IReadOnlyList<AgentSchedule> AgentSchedules, AgentControls AgentControls,
    IReadOnlyList<AgentNotification> Notifications, IReadOnlyList<ProviderResult> ProviderResults, IReadOnlyList<UsageReservation> Usage,
    IReadOnlyList<PrivacyJob> PrivacyJobs, IReadOnlyList<PersonalGoal> Goals, IReadOnlyList<DurableAgentTrigger> Triggers,
    IReadOnlyList<UsageReconciliation> Reconciliations, IReadOnlyList<ImportedDocument> Imports, IReadOnlyList<SourceClaim> SourceClaims,
    IReadOnlyList<VoiceRecall>? VoiceRecalls = null, IReadOnlyList<GbpCommitment>? GbpCommitments = null,
    IReadOnlyList<BudgetApproval>? BudgetApprovals = null, IReadOnlyList<BudgetManagementReceipt>? BudgetReceipts = null,
    IReadOnlyList<VoiceCoaching>? VoiceCoachings = null, IReadOnlyList<ProviderAttempt>? ProviderAttempts = null, DomainRegistryExport? Domains = null);
