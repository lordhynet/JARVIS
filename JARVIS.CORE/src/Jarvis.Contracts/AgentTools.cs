namespace Jarvis.Contracts;

public sealed record AgentToolGrant(Guid Id, Guid RunId, Guid ScopeId, long ScopeRevision, string ScopeKind,
    string CapabilityId, string CapabilityVersion, string DescriptorHash, IReadOnlyList<ReasoningDataClass> DataClasses,
    decimal MaximumUsd, int MaximumCalls, bool PreApproved, string GrantedBy, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt = null);

// Audit metadata only: arguments, model input, credentials and tool output are not duplicated here.
public sealed record AgentToolInvocation(Guid Id, Guid GrantId, Guid RunId, Guid TaskId, string ClaimHash,
    string CapabilityId, string CapabilityVersion, string ArgumentsHash, ReasoningDataClass DataClass,
    string Status, DateTimeOffset At, DateTimeOffset ExpiresAt, string? ApprovedBy = null,
    string? ApprovedHash = null, Guid? ReservationId = null, string? OutputHash = null, DateTimeOffset? CompletedAt = null);
