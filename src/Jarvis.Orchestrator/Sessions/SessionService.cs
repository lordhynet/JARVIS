using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Sessions;

/// <summary>Publishes session changes to connected devices (implemented with SignalR in Jarvis.Api).</summary>
public interface ISessionNotifier
{
    Task PublishAsync(SessionStatusEvent evt, CancellationToken ct = default);
}

public sealed class SessionValidationException(string message) : Exception(message);

/// <summary>Write-side business rules for session creation and checkpointing.</summary>
public sealed class SessionService(ISessionStore store, ISessionNotifier notifier, TimeProvider clock)
{
    public const int MaxTitleLength = 120;
    public const int MaxSummaryLength = 4000;

    public async Task<ConversationSession> CreateAsync(CreateSessionRequest request, CancellationToken ct = default)
    {
        var title = string.IsNullOrWhiteSpace(request.Title) ? DefaultTitle(request.Activity) : request.Title.Trim();
        if (title.Length > MaxTitleLength)
            throw new SessionValidationException($"Title must be {MaxTitleLength} characters or fewer.");
        if (!Enum.IsDefined(request.Activity))
            throw new SessionValidationException("Unknown activity type.");

        var now = clock.GetUtcNow();
        var session = new ConversationSession(
            Id: Guid.NewGuid(),
            Activity: request.Activity,
            Title: title,
            Status: SessionStatus.Active,
            ActiveDeviceId: string.IsNullOrWhiteSpace(request.DeviceId) ? null : request.DeviceId.Trim(),
            CurrentTask: null,
            CreatedAt: now,
            UpdatedAt: now,
            Checkpoints: []);

        session = await store.AddAsync(session, ct);
        await notifier.PublishAsync(ToEvent(SessionEventTypes.Created, session), ct);
        return session;
    }

    public async Task<ConversationSession?> CheckpointAsync(Guid sessionId, AddCheckpointRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Summary))
            throw new SessionValidationException("Summary is required.");
        if (request.Summary.Length > MaxSummaryLength)
            throw new SessionValidationException($"Summary must be {MaxSummaryLength} characters or fewer.");

        var now = clock.GetUtcNow();
        var updated = await store.AppendCheckpointAsync(
            sessionId,
            current => current.Status == SessionStatus.Ended
                ? throw new DomainException(409, "An ended session cannot be checkpointed.")
                : new SessionCheckpoint(
                Id: Guid.NewGuid(),
                SessionId: sessionId,
                Sequence: current.Checkpoints.Count + 1,
                Summary: request.Summary.Trim(),
                CurrentTask: string.IsNullOrWhiteSpace(request.CurrentTask) ? null : request.CurrentTask.Trim(),
                CreatedAt: now),
            now,
            ct);

        if (updated is not null)
            await notifier.PublishAsync(ToEvent(SessionEventTypes.Checkpointed, updated), ct);

        return updated;
    }

    private SessionStatusEvent ToEvent(string type, ConversationSession s) =>
        new(type, s.Id, s.Status, s.ActiveDeviceId, clock.GetUtcNow());

    private static string DefaultTitle(ActivityType activity) => activity switch
    {
        ActivityType.MockInterview => "Mock interview",
        ActivityType.LeetCode => "LeetCode practice",
        ActivityType.SystemDesign => "System design practice",
        ActivityType.French => "French practice",
        ActivityType.Briefing => "Briefing",
        ActivityType.WindDown => "Wind-down",
        ActivityType.Communication => "Communication practice",
        _ => "Conversation"
    };
}
