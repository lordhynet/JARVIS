namespace Jarvis.Contracts;

public sealed record GrantDeviceCapabilityRequest(string CapabilityId, string CapabilityVersion, string ConstraintsJson, DateTimeOffset ExpiresAt);
public sealed record DeviceCapabilityGrantView(Guid Id, string DeviceId, string CapabilityId, string CapabilityVersion,
    string ConstraintsJson, DateTimeOffset GrantedAt, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt);
