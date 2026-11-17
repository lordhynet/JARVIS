namespace Jarvis.Contracts;

/// <summary>Body for POST /api/sessions.</summary>
public sealed record CreateSessionRequest(
    ActivityType Activity,
    string? Title,
    string? DeviceId);

/// <summary>Body for POST /api/sessions/{id}/checkpoints.</summary>
public sealed record AddCheckpointRequest(
    string Summary,
    string? CurrentTask);

/// <summary>Pushed to every SignalR client on the status hub.</summary>
public sealed record SessionStatusEvent(
    string Type,
    Guid SessionId,
    SessionStatus Status,
    string? ActiveDeviceId,
    DateTimeOffset At);

public static class SessionEventTypes
{
    public const string Created = "session.created";
    public const string Checkpointed = "session.checkpointed";
}

public static class HubMethods
{
    /// <summary>Client-side method name that receives <see cref="SessionStatusEvent"/>.</summary>
    public const string SessionEvent = "sessionEvent";
}
