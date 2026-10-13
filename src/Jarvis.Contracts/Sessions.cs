namespace Jarvis.Contracts;

/// <summary>What a session is for. Drives which coaching module and model tier is used.</summary>
public enum ActivityType
{
    General,
    Briefing,
    LeetCode,
    SystemDesign,
    MockInterview,
    Communication,
    French,
    WindDown
}

public enum SessionStatus
{
    Active,
    Paused,
    Ended
}

/// <summary>
/// One logical conversation. It survives device changes and restarts:
/// the active device can change, the session ID does not.
/// </summary>
public sealed record ConversationSession(
    Guid Id,
    ActivityType Activity,
    string Title,
    SessionStatus Status,
    string? ActiveDeviceId,
    string? CurrentTask,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<SessionCheckpoint> Checkpoints);

/// <summary>
/// A resumable snapshot of where a session is. Written before any handoff
/// and periodically during long sessions.
/// </summary>
public sealed record SessionCheckpoint(
    Guid Id,
    Guid SessionId,
    int Sequence,
    string Summary,
    string? CurrentTask,
    DateTimeOffset CreatedAt);

public enum HandoffState
{
    Requested,
    Checkpointed,
    TargetReady,
    AudioTransferred,
    Completed,
    TimedOut,
    Failed
}

/// <summary>
/// Moving a live session between devices: request → checkpoint → target ready
/// → transfer audio ownership → confirm. Never two microphones answering at once.
/// </summary>
public sealed record HandoffRequest(
    Guid Id,
    Guid SessionId,
    string FromDeviceId,
    string ToDeviceId,
    HandoffState State,
    DateTimeOffset RequestedAt);
