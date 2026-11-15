using Jarvis.Contracts;

namespace Jarvis.Orchestrator.Sessions;

/// <summary>Read-side port. Query handlers cannot add or update sessions through this interface.</summary>
public interface ISessionReader
{
    async Task<SessionAudioState?> ReadAudioStateAsync(Guid id, CancellationToken ct = default)
    {
        var session = await GetAsync(id, ct);
        return session is null ? null : new(session.Status, session.ActiveDeviceId);
    }
    Task<ConversationSession?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<ConversationSession>> ListAsync(CancellationToken ct = default);
}
public sealed record SessionAudioState(SessionStatus Status, string? ActiveDeviceId);
