namespace Jarvis.Contracts;

public sealed record PrivacyStatus(long Epoch, int Revision, bool Erased, DateTimeOffset? ErasedAt, Guid? ErasureRequestId);
public sealed record EraseCoreRequest(Guid RequestId, int ExpectedRevision, string Confirmation);
public sealed record ReopenCoreRequest(int ExpectedRevision, string Confirmation);
public sealed record RetentionPolicy(bool Enabled = false, int SessionDays = 180, int AnswerDays = 180, int VoiceDays = 30,
    int ProviderCacheDays = 7, int ArtifactDays = 30, int SweepMinutes = 60);
public sealed record PrivacyCounts(int Sessions, int TrainingAnswers, int CoachingAnswers, int VoiceTurns, int ProviderResults,
    int WorkflowArtifacts, int AgentArtifacts, int Imports = 0, int DeviceGrants = 0, int DeviceCommands = 0, int DeviceReceipts = 0, int ContextMemories = 0,
    int InterviewRecordings = 0, int RecordingChunks = 0, long RecordingAudioBytes = 0,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] int ForgeProgrammes = 0);
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
    IReadOnlyList<VoiceCoaching>? VoiceCoachings = null, IReadOnlyList<ProviderAttempt>? ProviderAttempts = null, DomainRegistryExport? Domains = null,
    IReadOnlyList<AgentToolGrant>? AgentToolGrants = null, IReadOnlyList<AgentToolInvocation>? AgentToolInvocations = null,
    IReadOnlyList<AgentActionProposal>? AgentActions = null, IReadOnlyDictionary<Guid, DeviceVoiceAuthority>? DeviceVoiceAuthorities = null,
    IReadOnlyList<ContextMemoryItem>? ContextMemories = null,
    IReadOnlyList<TravelRoute>? TravelRoutes = null, IReadOnlyList<TravelLineState>? TravelLineStates = null,
    IReadOnlyList<InterviewRecording>? InterviewRecordings = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ForgeProgramme>? ForgeProgrammes = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ForgeProtectedRevisionExport>? ForgeProtectedRecords = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? ExportSensitivity = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? ExportStorageAdvice = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<LearningCreationExport>? CreationReceipts = null);

public sealed record LearningCreationExport(Guid RequestId, long Epoch, string Status,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? CreatedAt,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] Guid? CardId,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] LearningItem? Result,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? RetiredAt);

// Explicit authorized owner export only; not a display, agent, event or cache projection.
// Payload uses the versioned protected codec's property names and preserves historical values.
public sealed record ForgeProtectedRevisionExport(string Kind, string OwnerScope, Guid ProgrammeId, Guid Id,
    long Epoch, int Revision, Guid RequestId, int ExpectedRevision, DateTimeOffset CreatedAt, System.Text.Json.JsonElement Payload)
{
    public const string Sensitivity = "private-plaintext";
    public const string StorageAdvice = "This export contains plaintext private data. Store the downloaded file encrypted.";
}
