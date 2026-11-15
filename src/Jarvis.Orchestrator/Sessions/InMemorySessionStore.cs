using System.Collections.Concurrent;
using Jarvis.Contracts;

namespace Jarvis.Orchestrator.Sessions;

/// <summary>
/// Thread-safe in-memory store. Data is lost on restart — that is expected for
/// Milestone 1 and is what Milestone 2 (PostgreSQL) fixes.
/// </summary>
public sealed class InMemorySessionStore : ISessionStore
{
    private readonly ConcurrentDictionary<Guid, ConversationSession> _sessions = new();
    private readonly object _writeLock = new();
    public void ReplacePrivacyData(IReadOnlyList<ConversationSession> sessions)
    {
        lock (_writeLock) { _sessions.Clear(); foreach (var session in sessions) _sessions[session.Id] = session; }
    }

    public Task<ConversationSession?> UpdateAsync(Guid id,
        Func<ConversationSession, ConversationSession> update, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_writeLock)
        {
            if (!_sessions.TryGetValue(id, out var current)) return Task.FromResult<ConversationSession?>(null);
            var result = update(current);
            _sessions[id] = result;
            return Task.FromResult<ConversationSession?>(result);
        }
    }

    public Task<ConversationSession> AddAsync(ConversationSession session, CancellationToken ct = default)
    {
        if (!_sessions.TryAdd(session.Id, session))
            throw new InvalidOperationException($"Session {session.Id} already exists.");
        return Task.FromResult(session);
    }

    public Task<ConversationSession?> GetAsync(Guid id, CancellationToken ct = default) =>
        Task.FromResult(_sessions.TryGetValue(id, out var s) ? s : null);

    public Task<IReadOnlyList<ConversationSession>> ListAsync(CancellationToken ct = default)
    {
        IReadOnlyList<ConversationSession> list = _sessions.Values
            .OrderByDescending(s => s.UpdatedAt)
            .ToList();
        return Task.FromResult(list);
    }

    public Task<IReadOnlyList<PendingVoiceProjection>> ListPendingVoiceProjectionsAsync(int limit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<PendingVoiceProjection> pending = _sessions.Values
            .Where(s => s.PendingVoiceProjectionIds is { Count: > 0 })
            .Take(Math.Clamp(limit, 0, 1000))
            .Select(s => new PendingVoiceProjection(s.Id, s.PendingVoiceProjectionIds!)).ToArray();
        return Task.FromResult(pending);
    }

    public Task<ConversationSession?> AppendCheckpointAsync(
        Guid sessionId,
        Func<ConversationSession, SessionCheckpoint> createCheckpoint,
        DateTimeOffset now,
        CancellationToken ct = default)
    {
        // A single lock keeps checkpoint sequence numbers gap-free under concurrent writes.
        lock (_writeLock)
        {
            if (!_sessions.TryGetValue(sessionId, out var current))
                return Task.FromResult<ConversationSession?>(null);

            var checkpoint = createCheckpoint(current);
            var updated = current with
            {
                CurrentTask = checkpoint.CurrentTask ?? current.CurrentTask,
                UpdatedAt = now,
                Checkpoints = [.. current.Checkpoints, checkpoint]
            };
            _sessions[sessionId] = updated;
            return Task.FromResult<ConversationSession?>(updated);
        }
    }
}
