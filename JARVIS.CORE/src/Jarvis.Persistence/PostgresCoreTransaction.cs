using System.Diagnostics;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Microsoft.EntityFrameworkCore;
using Jarvis.Orchestrator.Domains;

namespace Jarvis.Persistence;

public sealed class PostgresCoreTransaction(IDbContextFactory<SessionDbContext> factory,
    AudioFenceChanges audioChanges) : ICoreTransaction
{
    public async Task<T> ExecuteAsync<T>(Func<ICoreTransactionWrites, Task<T>> operation, CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        await using var db = await factory.CreateDbContextAsync(ct);
        var phase = CoreTelemetry.RecordStoragePhase(started, "runtime.update.context");
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        phase = CoreTelemetry.RecordStoragePhase(phase, "runtime.update.begin");
        // Lock privacy and runtime first across all composite writes.
        await PostgresPrivacyGate.LockAsync(db, ct);
        phase = CoreTelemetry.RecordStoragePhase(phase, "runtime.update.privacy_gate");
        var sessions = new List<(ConversationSession Before, ConversationSession After)>();
        var changed = await PostgresRuntimeStore.UpdateInTransactionAsync(db,
            state => operation(new Writes(db, state, sessions, ct)), started, phase, ct);
        await transaction.CommitAsync(ct);
        CoreTelemetry.RecordStoragePhase(changed.SavedAt, "runtime.update.commit");
        audioChanges.LeaseChanged(changed.Before, changed.After);
        foreach (var (before, after) in sessions) audioChanges.SessionChanged(before, after);
        return changed.Result;
    }

    internal sealed class Writes(SessionDbContext db, RuntimeState runtime,
        List<(ConversationSession Before, ConversationSession After)> sessions, CancellationToken ct) : ICoreTransactionWrites
    {
        public RuntimeState Runtime => runtime;
        public async Task<DomainResolved?> ReadActiveDomainAsync(string key) => DomainRegistryProjection.Active(await PostgresDomainRegistryStore.Snapshot(db, ct), key);

        public Task<SessionAudioState?> ReadAudioStateAsync(Guid sessionId) =>
            PostgresSessionStore.ReadAudioStateAsync(db, sessionId, ct, lockSession: true);

        public async Task<ConversationSession?> UpdateSessionAsync(Guid sessionId, Func<ConversationSession, ConversationSession> update)
        {
            var changed = await PostgresSessionStore.UpdateInTransactionAsync(db, sessionId, update, ct);
            if (changed is { } completed) sessions.Add(completed);
            return changed?.After;
        }

        public Task<DeviceGrant> AddDeviceGrantAsync(DeviceGrant grant) => PostgresDeviceGrantStore.AddAsync(db, grant, ct);

        public Task<int> RevokeDeviceGrantsAsync(string deviceId, DateTimeOffset now) =>
            PostgresDeviceGrantStore.RevokeForDeviceAsync(db, deviceId, now, ct);

        public Task<DeviceGrant?> ReadDeviceGrantAsync(Guid id) => PostgresDeviceGrantStore.ReadLockedAsync(db, id, ct);

        public Task<DeviceCommand?> ReadDeviceCommandAsync(Guid id) => PostgresDeviceCommandStore.ReadLockedAsync(db, id, ct);

        public Task<DeviceCommand?> ReadDeviceDispatchAsync(string deviceId, string credentialHash, Guid requestId) =>
            PostgresDeviceCommandStore.ReadDispatchAsync(db, deviceId, credentialHash, requestId, ct);

        public Task<IReadOnlyList<DeviceCommand>> ReadPendingDeviceCommandsAsync(string deviceId, string credentialHash,
            DateTimeOffset now, DeviceCommand? after) =>
            PostgresDeviceCommandStore.ReadPendingAsync(db, deviceId, credentialHash, now, after, ct);

        public Task SaveDeviceCommandAsync(DeviceCommand command) => PostgresDeviceCommandStore.SaveAsync(db, command, ct);

        public Task<IReadOnlyList<DeviceReceiptView>> ReadDeviceReceiptsAsync(Guid commandId) =>
            PostgresDeviceReceiptStore.ListAsync(db, commandId, ct);

        public async Task AddDeviceReceiptAsync(DeviceReceiptView receipt)
        {
            db.DeviceReceipts.Add(DeviceReceiptRow.From(receipt));
            await db.SaveChangesAsync(ct);
        }

        public async Task<bool> HasHomeDispatchCapacityAsync(string deviceId, string entityId, DateTimeOffset since, int limit)
        {
            var recent = db.DeviceCommands.Where(x => x.HomeEntityId != null && x.DispatchedAt > since);
            return await recent.CountAsync(x => x.DeviceId == deviceId, ct) < limit &&
                await recent.CountAsync(x => x.HomeEntityId == entityId, ct) < limit;
        }
    }
}
