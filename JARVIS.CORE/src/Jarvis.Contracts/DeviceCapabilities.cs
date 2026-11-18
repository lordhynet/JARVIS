namespace Jarvis.Contracts;

public sealed record GrantDeviceCapabilityRequest(string CapabilityId, string CapabilityVersion, string ConstraintsJson, DateTimeOffset ExpiresAt,
    Guid RequestId = default, string? Reason = null, bool PreApproved = false, bool AllowWhenLocked = false);

public sealed record DeviceGrantAudit(Guid RequestId, string GrantedBy, string Reason);

public sealed record DeviceCapabilityGrantView(Guid Id, string DeviceId, string CapabilityId, string CapabilityVersion,
    string ConstraintsJson, DateTimeOffset GrantedAt, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt,
    DeviceGrantAudit? Audit = null, bool Effective = false, bool PreApproved = false, bool AllowWhenLocked = false);
