using Jarvis.Contracts;
using Jarvis.Orchestrator.Sessions;

namespace Jarvis.Orchestrator.Runtime;

public interface ICoreTransaction
{
    // Use the supplied writes sequentially. Publish external notifications after commit.
    Task<T> ExecuteAsync<T>(Func<ICoreTransactionWrites, Task<T>> operation, CancellationToken ct = default);
}

public interface ICoreTransactionWrites
{
    RuntimeState Runtime { get; }
    Task<SessionAudioState?> ReadAudioStateAsync(Guid sessionId);
    Task<ConversationSession?> UpdateSessionAsync(Guid sessionId, Func<ConversationSession, ConversationSession> update);
    Task<DeviceGrant> AddDeviceGrantAsync(DeviceGrant grant);
    Task<int> RevokeDeviceGrantsAsync(string deviceId, DateTimeOffset now);
}

public sealed class MemoryCoreTransaction(InMemoryRuntimeStore runtime, InMemorySessionStore sessions,
    MemoryPrivacyBoundary boundary) : ICoreTransaction
{
    public Task<T> ExecuteAsync<T>(Func<ICoreTransactionWrites, Task<T>> operation, CancellationToken ct = default) =>
        boundary.Run(async () =>
        {
            var state = await runtime.ReadAsync(x => x, ct);
            var writes = new Writes(state, sessions, boundary, ct);
            var result = await operation(writes);
            ct.ThrowIfCancellationRequested();
            runtime.ReplacePrivacyData(state);
            writes.Commit();
            return result;
        });

    private sealed class Writes(RuntimeState state, InMemorySessionStore sessions, MemoryPrivacyBoundary boundary,
        CancellationToken ct) : ICoreTransactionWrites
    {
        private readonly Dictionary<Guid, ConversationSession> stagedSessions = [];
        private Dictionary<Guid, DeviceGrant>? stagedGrants;

        public RuntimeState Runtime => state;

        public async Task<SessionAudioState?> ReadAudioStateAsync(Guid sessionId)
        {
            var session = await ReadSessionAsync(sessionId);
            return session is null ? null : new(session.Status, session.ActiveDeviceId);
        }

        public async Task<ConversationSession?> UpdateSessionAsync(Guid sessionId, Func<ConversationSession, ConversationSession> update)
        {
            var current = await ReadSessionAsync(sessionId);
            if (current is null) return null;
            var changed = update(current);
            stagedSessions[sessionId] = changed;
            return changed;
        }

        public Task<DeviceGrant> AddDeviceGrantAsync(DeviceGrant grant) =>
            Task.FromResult(InMemoryDeviceGrantStore.Add(Grants(), grant));

        public Task<int> RevokeDeviceGrantsAsync(string deviceId, DateTimeOffset now) =>
            Task.FromResult(InMemoryDeviceGrantStore.RevokeForDevice(Grants(), deviceId, now));

        public void Commit()
        {
            if (stagedGrants is not null) boundary.DeviceGrants = stagedGrants;
            foreach (var (id, session) in stagedSessions)
                sessions.UpdateAsync(id, _ => session).GetAwaiter().GetResult();
        }

        private Task<ConversationSession?> ReadSessionAsync(Guid id)
        {
            ct.ThrowIfCancellationRequested();
            return stagedSessions.TryGetValue(id, out var staged)
                ? Task.FromResult<ConversationSession?>(staged) : sessions.GetAsync(id, ct);
        }

        private Dictionary<Guid, DeviceGrant> Grants() => stagedGrants ??= new(boundary.DeviceGrants);
    }
}
