using Jarvis.Contracts;
using Jarvis.Orchestrator.Sessions;
using Jarvis.Orchestrator.Domains;

namespace Jarvis.Orchestrator.Runtime;

public interface ICoreTransaction
{
    // Use the supplied writes sequentially. Publish external notifications after commit.
    Task<T> ExecuteAsync<T>(Func<ICoreTransactionWrites, Task<T>> operation, CancellationToken ct = default);
}

public interface ICoreTransactionWrites
{
    const int DeviceCommandBatchSize = 16;
    RuntimeState Runtime { get; }
    Task<DomainResolved?> ReadActiveDomainAsync(string key) => Task.FromResult(DomainRegistryProjection.Active(Runtime.DomainRegistry, key));
    Task<SessionAudioState?> ReadAudioStateAsync(Guid sessionId);
    Task<ConversationSession?> UpdateSessionAsync(Guid sessionId, Func<ConversationSession, ConversationSession> update);
    Task<DeviceGrant> AddDeviceGrantAsync(DeviceGrant grant);
    Task<int> RevokeDeviceGrantsAsync(string deviceId, DateTimeOffset now);
    Task<DeviceGrant?> ReadDeviceGrantAsync(Guid id);
    Task<DeviceCommand?> ReadDeviceCommandAsync(Guid id);
    Task<DeviceCommand?> ReadDeviceDispatchAsync(string deviceId, string credentialHash, Guid requestId);
    Task<IReadOnlyList<DeviceCommand>> ReadPendingDeviceCommandsAsync(string deviceId, string credentialHash, DateTimeOffset now,
        DeviceCommand? after);
    Task SaveDeviceCommandAsync(DeviceCommand command);
    Task<IReadOnlyList<DeviceReceiptView>> ReadDeviceReceiptsAsync(Guid commandId);
    Task AddDeviceReceiptAsync(DeviceReceiptView receipt);
    Task<bool> HasHomeDispatchCapacityAsync(string deviceId, string entityId, DateTimeOffset since, int limit);
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
        private readonly Dictionary<Guid, DeviceCommand> stagedCommands = [];
        private readonly List<DeviceReceiptView> stagedReceipts = [];

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

        public Task<DeviceGrant?> ReadDeviceGrantAsync(Guid id) =>
            Task.FromResult((stagedGrants ?? boundary.DeviceGrants).GetValueOrDefault(id));

        public Task<DeviceCommand?> ReadDeviceCommandAsync(Guid id) =>
            Task.FromResult(stagedCommands.GetValueOrDefault(id) ?? boundary.DeviceCommands.GetValueOrDefault(id));

        public Task<DeviceCommand?> ReadDeviceDispatchAsync(string deviceId, string credentialHash, Guid requestId) =>
            Task.FromResult(DeviceCommands().SingleOrDefault(x => x.DeviceId == deviceId && x.CredentialHash == credentialHash &&
                x.DispatchRequestId == requestId));

        public Task<IReadOnlyList<DeviceCommand>> ReadPendingDeviceCommandsAsync(string deviceId, string credentialHash,
            DateTimeOffset now, DeviceCommand? after) => Task.FromResult<IReadOnlyList<DeviceCommand>>(DeviceCommands()
                .Where(x => x.DeviceId == deviceId && x.CredentialHash == credentialHash &&
                    x.Status == DeviceCommandStatus.Pending && x.ExpiresAt > now &&
                    (after is null || x.CreatedAt > after.CreatedAt || x.CreatedAt == after.CreatedAt && x.Id.CompareTo(after.Id) > 0))
                .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).Take(ICoreTransactionWrites.DeviceCommandBatchSize).ToArray());

        private IEnumerable<DeviceCommand> DeviceCommands() =>
            stagedCommands.Values.Concat(boundary.DeviceCommands.Values.Where(x => !stagedCommands.ContainsKey(x.Id)));

        public Task<bool> HasHomeDispatchCapacityAsync(string deviceId, string entityId, DateTimeOffset since, int limit)
        {
            var recent = DeviceCommands().Where(x => x.HomeEntityId is not null && x.DispatchedAt > since).ToArray();
            return Task.FromResult(recent.Count(x => x.DeviceId == deviceId) < limit && recent.Count(x => x.HomeEntityId == entityId) < limit);
        }

        public Task SaveDeviceCommandAsync(DeviceCommand command)
        {
            stagedCommands[command.Id] = command;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DeviceReceiptView>> ReadDeviceReceiptsAsync(Guid commandId) =>
            Task.FromResult<IReadOnlyList<DeviceReceiptView>>((boundary.DeviceReceipts.GetValueOrDefault(commandId) ?? [])
                .Concat(stagedReceipts.Where(x => x.CommandId == commandId)).ToArray());

        public Task AddDeviceReceiptAsync(DeviceReceiptView receipt)
        {
            stagedReceipts.Add(receipt);
            return Task.CompletedTask;
        }

        public void Commit()
        {
            if (stagedGrants is not null) boundary.DeviceGrants = stagedGrants;
            foreach (var (id, command) in stagedCommands) boundary.DeviceCommands[id] = command;
            foreach (var receipt in stagedReceipts)
            {
                if (!boundary.DeviceReceipts.TryGetValue(receipt.CommandId, out var receipts))
                    boundary.DeviceReceipts[receipt.CommandId] = receipts = [];
                receipts.Add(receipt);
            }
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
