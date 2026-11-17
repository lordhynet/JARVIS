using System.Text.Json.Serialization;

namespace Jarvis.Contracts;

public sealed record BudgetFxQuote(string Source, decimal UsdToGbp, DateTimeOffset ObservedAt, DateTimeOffset ExpiresAt,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateOnly? PublishedOn = null);
public sealed record BudgetFxStatus(bool AutomaticEnabled, bool UsingAutomaticQuote, DateTimeOffset? LastAttemptAt, string? LastError,
    BudgetFxQuote? LastAcceptedQuote = null);
public sealed record GbpBudgetPolicy(bool Enabled = false, string ScopeId = "", string Environment = "Development",
    string Version = "1", BudgetFxQuote? Fx = null, decimal? TaxMultiplier = null, decimal? SafetyMultiplier = null,
    BudgetFxStatus? FxStatus = null);
public sealed record GbpCommitment(Guid ReservationId, string ScopeId, string Environment, string Month,
    string PolicyHash, BudgetFxQuote Fx, decimal TaxMultiplier, decimal SafetyMultiplier,
    decimal ReservedGbp, decimal? AccountedGbp = null, bool Legacy = false, bool GbpVerified = false);
public sealed record BudgetApproval(Guid Id, string ScopeId, string Environment, string Month, string PolicyHash,
    decimal CeilingGbp, DateTimeOffset ExpiresAt, long Epoch, string ActorId, DateTimeOffset At, bool Revoked = false);
public sealed record BudgetManagementReceipt(Guid Id, string Kind, Guid TargetId, string Fingerprint,
    long Epoch, string ActorId, DateTimeOffset At, string Evidence = "");
public sealed record BudgetApprovalRequest(Guid RequestId, string PolicyHash, string Month, decimal CeilingGbp,
    DateTimeOffset ExpiresAt, int ExpectedRevision, string Confirmation);
public sealed record RevokeBudgetApprovalRequest(Guid RequestId, int ExpectedRevision, string Confirmation);
public sealed record ClassifyLegacyChargeRequest(Guid RequestId, string PolicyHash, string ConversionHash,
    decimal ExpectedUsd, string Evidence, string Confirmation);
public sealed record GbpBudgetStatus(bool Ready, string Detail, string ScopeId, string Environment, string Month,
    string PolicyHash, string ConversionHash, decimal CeilingGbp, decimal CommittedGbp, int UnclassifiedCharges,
    int Revision, IReadOnlyList<BudgetApproval> Approvals, GbpBudgetPolicy? ConversionBasis = null);
public sealed record LegacyChargePreview(Guid ReservationId, decimal ExpectedUsd, string ConversionHash, GbpCommitment ProjectedCommitment);
