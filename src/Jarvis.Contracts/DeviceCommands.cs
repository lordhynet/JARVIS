namespace Jarvis.Contracts;

public enum DeviceCommandStatus
{
    Pending = 0,
    AwaitingConfirmation = 1,
    Dispatched = 2,
    Acknowledged = 3,
    Succeeded = 4,
    Failed = 5,
    Expired = 6,
    Cancelled = 7
}

public sealed record ProposeDeviceCommandRequest(Guid RequestId, Guid GrantId, string ArgumentsJson, DateTimeOffset ExpiresAt);
public sealed record ConfirmDeviceCommandRequest(string ArgumentHash, bool? Approve);
public sealed record ClaimDeviceCommandRequest(Guid RequestId);
public sealed record DeliveredDeviceCommand(Guid Id, string CapabilityId, string CapabilityVersion, string ArgumentsJson,
    string ArgumentHash, DateTimeOffset ExpiresAt, DateTimeOffset DispatchedAt, Guid DispatchRequestId = default);
public sealed record DeviceCommandView(Guid Id, Guid GrantId, string DeviceId, string CapabilityId, string CapabilityVersion,
    string ArgumentsJson, string ArgumentHash, string Origin, DeviceCommandStatus Status, DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt, DateTimeOffset? ConfirmedAt, string? ConfirmedBy, DateTimeOffset? CancelledAt, string? CancelledBy,
    DateTimeOffset? DispatchedAt = null);
