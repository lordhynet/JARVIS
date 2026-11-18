using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jarvis.Contracts;

public enum DomainStatus { Draft, PendingApproval, Active, Disabled }
public enum DomainExecutionMode { Template, Assisted }
public enum DomainRiskClass { General, Finance, Law, Health }
public sealed record DomainCapabilityRequirement(string Id, string VersionConstraint = "1", bool IsRequired = true);
public sealed record DomainManifest(int SchemaVersion, string Key, string DisplayName, string Description,
    IReadOnlyList<string> Aliases, IReadOnlyList<AgentRole> Roles, IReadOnlyList<DomainCapabilityRequirement> Capabilities,
    IReadOnlyList<string> PracticePrompts, DomainExecutionMode ExecutionMode, bool UsesPersonalContext = false,
    IReadOnlyDictionary<string, string>? Configuration = null, string? ParentKey = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? StudyGuidance = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<DomainKnowledgeReference>? KnowledgeSources = null);
public sealed record DomainKnowledgeReference(string Kind, string Reference);
public sealed record DomainDefinition(Guid Id, string Key, string WorkflowKey, string DisplayName, DomainStatus Status,
    Guid? CurrentVersionId, Guid LatestVersionId, int Revision, bool BuiltIn, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record DomainVersion(Guid Id, Guid DomainId, int Version, int SchemaVersion, DomainManifest Manifest,
    string ManifestHash, string CreatedBy, DateTimeOffset CreatedAt);
public sealed record DomainCapabilityBinding(Guid DomainVersionId, string CapabilityId, string VersionConstraint, bool IsRequired);
public sealed record DomainApproval(Guid Id, Guid DomainVersionId, string SubjectId, string Decision, string PolicyVersion,
    DateTimeOffset At, Guid RequestId, string Fingerprint, int AppliedRevision, string Reason, DomainRiskClass? RiskClass = null);
public sealed record DomainPreferences(string Language = "en-GB", int DailyMinutes = 15, bool Notifications = false);
public sealed record UserDomainState(string SubjectId, Guid DomainId, bool Enabled, DomainPreferences Preferences,
    Guid? ProgressRef, int Revision, DateTimeOffset UpdatedAt);
public sealed record DomainAuditEvent(Guid Id, Guid DomainId, Guid? VersionId, string Action, string Actor,
    Guid RequestId, DateTimeOffset OccurredAt, string DetailsJson);
public sealed record DomainPreview(DomainDefinition Definition, DomainVersion Version, DomainRiskClass Risk,
    IReadOnlyList<string> UnmetDependencies, string MemoryScope, bool SharesPersonalContext, decimal? EstimatedAiGbp = null);
public sealed record DomainResolved(DomainDefinition Definition, DomainVersion Version, long Epoch, bool HasCurrentApproval = false,
    DomainRiskClass? ApprovedRiskClass = null);
public sealed record DomainOperationReceipt(Guid RequestId, Guid DomainId, string Key, Guid VersionId,
    int Revision, string ManifestHash, DateTimeOffset At, string Action);
public sealed record DomainDraftProvenance(Guid GenerationId, string RequestFingerprint, string Source,
    Guid? SessionId = null, Guid? UtteranceId = null, string? Provider = null, string? Model = null);
public sealed record SaveDomainDraftRequest(Guid RequestId, int? ExpectedRevision, JsonElement Manifest, DomainDraftProvenance? Provenance = null);
public sealed record ProposeDomainRequest(Guid RequestId, string Description, bool AllowCloudProcessing, string? Key = null, int? ExpectedRevision = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<DomainKnowledgeReference>? KnowledgeSources = null);
public sealed record DomainProposal(DomainPreview Preview, bool ExistingDomain, DomainDraftProvenance? Provenance);
public sealed record ActivateDomainRequest(Guid RequestId, int ExpectedRevision, Guid VersionId, string ManifestHash, string Reason = "",
    DomainRiskClass? RiskClass = null);
public sealed record DisableDomainRequest(Guid RequestId, int ExpectedRevision, string Reason = "");
public sealed record SaveDomainStateRequest(int ExpectedRevision, bool Enabled, DomainPreferences Preferences, Guid? ProgressRef = null);
public sealed record DomainPracticeTemplate(string Title, IReadOnlyList<string> Prompts);
public sealed record DomainRegistryExport(IReadOnlyList<DomainDefinition> Definitions, IReadOnlyList<DomainVersion> Versions,
    IReadOnlyList<DomainCapabilityBinding> Bindings, IReadOnlyList<DomainApproval> Approvals,
    IReadOnlyList<UserDomainState> UserStates, IReadOnlyList<DomainAuditEvent> AuditEvents);
