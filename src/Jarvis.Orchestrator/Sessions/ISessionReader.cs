using Jarvis.Contracts;

namespace Jarvis.Orchestrator.Sessions;

/// <summary>Read-side port. Query handlers cannot add or update sessions through this interface.</summary>
public interface ISessionReader
{
    async Task<SessionHandoff?> ReadHandoffAsync(Guid id, CancellationToken ct = default) => (await GetAsync(id, ct))?.Handoff;
    async Task<IReadOnlyList<SessionPreparationState>> ListPreparationAsync(CancellationToken ct = default) =>
        (await ListAsync(ct)).Select(s => new SessionPreparationState(s.Id, s.Status, s.ActiveDeviceId, s.Title, s.UpdatedAt,
            s.Handoff, s.Checkpoints.Select(c => c.Id).ToArray(), s.Messages?.Count ?? 0)).ToArray();
    async Task<SessionAudioState?> ReadAudioStateAsync(Guid id, CancellationToken ct = default)
    {
        var session = await GetAsync(id, ct);
        return session is null ? null : new(session.Status, session.ActiveDeviceId);
    }
    Task<ConversationSession?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<ConversationSession>> ListAsync(CancellationToken ct = default);
}
public sealed record SessionAudioState(SessionStatus Status, string? ActiveDeviceId);
public sealed record SessionPreparationState(Guid Id, SessionStatus Status, string? ActiveDeviceId, string Title,
    DateTimeOffset UpdatedAt, SessionHandoff? Handoff, IReadOnlyList<Guid> CheckpointIds, int MessageCount);
