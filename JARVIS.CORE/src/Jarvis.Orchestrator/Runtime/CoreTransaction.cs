using Jarvis.Contracts;
using Jarvis.Orchestrator.Sessions;
using Jarvis.Orchestrator.Domains;
using Jarvis.Orchestrator.Forge;

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
    Task<ForgeProgramme?> ReadForgeProgrammeAsync(string ownerScope, Guid id);
    Task<int> CountForgeProgrammesAsync(string ownerScope);
    Task SaveForgeProgrammeAsync(ForgeProgramme programme);
    Task<ForgeProtectedRecord?> ReadForgeProtectedRecordAsync(ForgeProtectedKind kind, string ownerScope, Guid programmeId, Guid id);
    Task<ForgeProtectedRecord?> ReadForgeProtectedRequestAsync(ForgeProtectedKind kind, string ownerScope, Guid programmeId, Guid requestId);
    Task AppendForgeProtectedRecordAsync(ForgeProtectedRecord record);
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
    // Tombstones every unpurged generated practice question in the given conversations inside this transaction and returns
    // their IDs so the caller purges runtime copies too. No permissive default: a writer without the table refuses.
    Task<IReadOnlySet<Guid>> TombstoneGeneratedQuestionsInAsync(IReadOnlySet<Guid> sessions, DateTimeOffset at) =>
        throw new DomainException(503, "Generated practice storage is unavailable in this transaction.");
}

public sealed class MemoryCoreTransaction(InMemoryRuntimeStore runtime, InMemorySessionStore sessions,
    MemoryPrivacyBoundary boundary) : ICoreTransaction
{
    public Task<T> ExecuteAsync<T>(Func<ICoreTransactionWrites, Task<T>> operation, CancellationToken ct = default) =>
        boundary.Run(async () =>
        {
            var state = await runtime.ReadAsync(x => x, ct);
            var writes = new Writes(state, runtime, sessions, boundary, ct);
            var result = await operation(writes);
            ct.ThrowIfCancellationRequested();
            runtime.ReplacePrivacyData(state);
            writes.Commit();
            return result;
        });

    private sealed class Writes(RuntimeState state, InMemoryRuntimeStore runtime, InMemorySessionStore sessions, MemoryPrivacyBoundary boundary,
        CancellationToken ct) : ICoreTransactionWrites
    {
        private readonly Dictionary<Guid, ConversationSession> stagedSessions = [];
        private readonly Dictionary<Guid, DateTimeOffset> stagedGeneratedTombstones = [];
        public Task<IReadOnlySet<Guid>> TombstoneGeneratedQuestionsInAsync(IReadOnlySet<Guid> sessions, DateTimeOffset at)
        {
            var ids = runtime.GeneratedQuestionsIn(sessions);
            foreach (var id in ids) stagedGeneratedTombstones.TryAdd(id, at);
            return Task.FromResult(ids);
        }
        private readonly Dictionary<(string Owner, Guid Id), ForgeProgramme> stagedForge = [];
        private readonly List<ForgeProtectedRecord> stagedProtected = [];
        private Dictionary<Guid, DeviceGrant>? stagedGrants;
        private readonly Dictionary<Guid, DeviceCommand> stagedCommands = [];
        private readonly List<DeviceReceiptView> stagedReceipts = [];

        public RuntimeState Runtime => state;
        public Task<ForgeProgramme?> ReadForgeProgrammeAsync(string ownerScope, Guid id) =>
            Task.FromResult(stagedForge.GetValueOrDefault((ownerScope, id)) ?? boundary.ForgeProgrammes.GetValueOrDefault((ownerScope, id)));
        public Task<int> CountForgeProgrammesAsync(string ownerScope) => Task.FromResult(
            boundary.ForgeProgrammes.Values.Where(x => !stagedForge.ContainsKey((x.OwnerScope, x.Id))).Concat(stagedForge.Values)
                .Count(x => x.OwnerScope == ownerScope && x.DeletedAt is null));
        public Task SaveForgeProgrammeAsync(ForgeProgramme programme) {
            stagedForge[(programme.OwnerScope, programme.Id)] = programme;
            return Task.CompletedTask;
        }

        private IEnumerable<ForgeProtectedRecord> ProtectedRecords(ForgeProtectedKind kind, string owner, Guid programme) =>
            boundary.ForgeProtectedRecords.Concat(stagedProtected).Where(x => x.Kind == kind && x.OwnerScope == owner && x.ProgrammeId == programme);
        public async Task<ForgeProtectedRecord?> ReadForgeProtectedRecordAsync(ForgeProtectedKind kind, string ownerScope, Guid programmeId, Guid id) =>
            await ReadForgeProgrammeAsync(ownerScope, programmeId) is { DeletedAt: null }
                ? ProtectedRecords(kind, ownerScope, programmeId).Where(x => x.Id == id).OrderByDescending(x => x.Revision).FirstOrDefault()?.CopyCiphertext() : null;
        public async Task<ForgeProtectedRecord?> ReadForgeProtectedRequestAsync(ForgeProtectedKind kind, string ownerScope, Guid programmeId, Guid requestId) =>
            await ReadForgeProgrammeAsync(ownerScope, programmeId) is { DeletedAt: null }
                ? ProtectedRecords(kind, ownerScope, programmeId).SingleOrDefault(x => x.RequestId == requestId)?.CopyCiphertext() : null;
        public async Task AppendForgeProtectedRecordAsync(ForgeProtectedRecord record) {
            ct.ThrowIfCancellationRequested(); record.Validate(boundary.Status.Epoch);
            Guard.Require(await ReadForgeProgrammeAsync(record.OwnerScope, record.ProgrammeId) is { DeletedAt: null }, 404, "Forge programme not found.");
            var latest = await ReadForgeProtectedRecordAsync(record.Kind, record.OwnerScope, record.ProgrammeId, record.Id);
            Guard.Require((latest?.Revision ?? 0) == record.ExpectedRevision, 409, "Protected Forge record changed. Reload before editing.");
            Guard.Require(await ReadForgeProtectedRequestAsync(record.Kind, record.OwnerScope, record.ProgrammeId, record.RequestId) is null,
                409, "Protected Forge request ID is already committed.");
            stagedProtected.Add(record.CopyCiphertext());
        }

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
            foreach (var group in stagedGeneratedTombstones.GroupBy(p => p.Value)) runtime.TombstoneGeneratedQuestions(group.Select(p => p.Key).ToHashSet(), group.Key);
            foreach (var (key, programme) in stagedForge) boundary.ForgeProgrammes[key] = programme;
            boundary.ForgeProtectedRecords.AddRange(stagedProtected);
            // Remove every encrypted revision and its retry material, including newly staged values.
            boundary.ForgeProtectedRecords.RemoveAll(x => boundary.ForgeProgrammes.GetValueOrDefault((x.OwnerScope, x.ProgrammeId)) is not { DeletedAt: null });
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
