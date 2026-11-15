using Jarvis.Contracts;

namespace Jarvis.Orchestrator.Sessions;

/// <summary>Read-side port. Query handlers cannot add or update sessions through this interface.</summary>
public interface ISessionReader
{
    async Task<SessionHandoff?> ReadHandoffAsync(Guid id, CancellationToken ct = default) => (await GetAsync(id, ct))?.Handoff;
    async Task<IReadOnlyList<SessionPreparationState>> ListPreparationAsync(CancellationToken ct = default) =>
        (await ListAsync(ct)).Select(s => new SessionPreparationState(s.Id, s.Status, s.ActiveDeviceId, s.Title, s.UpdatedAt,
            s.Handoff, s.Checkpoints.Select(c => c.Id).ToArray(), s.Messages?.Count ?? 0)).ToArray();
    async Task<IReadOnlyList<SessionPreparationState>> ListPreparationAsync(string deviceId, Guid preferredCheckpointId, CancellationToken ct = default) =>
        (await ListPreparationAsync(ct)).Where(s => s.Id == preferredCheckpointId || s.CheckpointIds.Contains(preferredCheckpointId) ||
            s.ActiveDeviceId == deviceId || s.Status == SessionStatus.Paused && s.ActiveDeviceId is null && s.Title == "JARVIS voice" ||
            s.Status == SessionStatus.Active && s.Handoff?.ToDeviceId == deviceId).ToArray();
    async Task<SessionAudioState?> ReadAudioStateAsync(Guid id, CancellationToken ct = default)
    {
        var session = await GetAsync(id, ct);
        return session is null ? null : new(session.Status, session.ActiveDeviceId);
    }
    Task<ConversationSession?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<ConversationSession>> ListAsync(CancellationToken ct = default);
    Task<IReadOnlyList<PendingVoiceProjection>> ListPendingVoiceProjectionsAsync(int limit, CancellationToken ct = default);
}
public sealed record PendingVoiceProjection(Guid SessionId, IReadOnlyList<Guid> RequestIds);
public sealed record SessionAudioState(SessionStatus Status, string? ActiveDeviceId);
public sealed record SessionPreparationState(Guid Id, SessionStatus Status, string? ActiveDeviceId, string Title,
    DateTimeOffset UpdatedAt, SessionHandoff? Handoff, IReadOnlyList<Guid> CheckpointIds, int MessageCount);
