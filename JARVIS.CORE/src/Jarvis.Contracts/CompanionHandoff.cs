namespace Jarvis.Contracts;

// Transport controls only: no conversation text, checkpoints, business data or tickets.
public sealed record CompanionHandoffRequest(string DeviceId, string Action = "poll", Guid? SessionId = null,
    Guid? HandoffId = null, string? TargetDeviceId = null, Guid? RequestId = null, bool? Locked = null, bool AudioStopped = false);
public sealed record CompanionHandoffTarget(string DeviceId, string Name, DeviceKind Kind, bool Browser = false);
public sealed record CompanionHandoffItem(Guid SessionId, Guid HandoffId, string FromDeviceId, string ToDeviceId, HandoffState State, DateTimeOffset ExpiresAt, bool Incoming = false);
public sealed record CompanionHandoffView(Guid? ActiveSessionId, IReadOnlyList<CompanionHandoffItem> Offers, IReadOnlyList<CompanionHandoffTarget> Targets);
