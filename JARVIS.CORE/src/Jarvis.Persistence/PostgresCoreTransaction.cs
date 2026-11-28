using System.Diagnostics;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Microsoft.EntityFrameworkCore;
using Jarvis.Orchestrator.Domains;
using Jarvis.Orchestrator.Forge;

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
        var privacy = await PostgresPrivacyGate.LockAsync(db, ct);
        phase = CoreTelemetry.RecordStoragePhase(phase, "runtime.update.privacy_gate");
        var sessions = new List<(ConversationSession Before, ConversationSession After)>();
        var changed = await PostgresRuntimeStore.UpdateInTransactionAsync(db,
            state => operation(new Writes(db, state, sessions, privacy.Epoch, ct)), started, phase, ct);
        await transaction.CommitAsync(ct);
        CoreTelemetry.RecordStoragePhase(changed.SavedAt, "runtime.update.commit");
        audioChanges.LeaseChanged(changed.Before, changed.After);
        foreach (var (before, after) in sessions) audioChanges.SessionChanged(before, after);
        return changed.Result;
    }

    internal sealed class Writes(SessionDbContext db, RuntimeState runtime,
        List<(ConversationSession Before, ConversationSession After)> sessions, long epoch, CancellationToken ct) : ICoreTransactionWrites
    {
        public RuntimeState Runtime => runtime;
        // Tracked row updates are saved by this transaction's own SaveChanges, so tombstones commit with the runtime change.
        public async Task<IReadOnlySet<Guid>> TombstoneGeneratedQuestionsInAsync(IReadOnlySet<Guid> sessions, DateTimeOffset at)
        {
            var ids = sessions.ToArray();
            var rows = await db.GeneratedPracticeQuestions.Where(r => ids.Contains(r.SessionId) && r.PurgedAt == null).ToListAsync(ct);
            foreach (var row in rows) { row.Json = null; row.Fingerprint = ""; row.Lineage = "[]"; row.PurgedAt = at; }
            return rows.Select(r => r.Id).ToHashSet();
        }
        public async Task<ForgeProgramme?> ReadForgeProgrammeAsync(string ownerScope, Guid id) =>
            (db.ForgeProgrammes.Local.SingleOrDefault(x => x.OwnerScope == ownerScope && x.Id == id) ??
                await db.ForgeProgrammes.SingleOrDefaultAsync(x => x.OwnerScope == ownerScope && x.Id == id, ct))?.Contract();
        public Task<int> CountForgeProgrammesAsync(string ownerScope) => db.ForgeProgrammes.CountAsync(x => x.OwnerScope == ownerScope && x.DeletedAt == null, ct);
        public async Task SaveForgeProgrammeAsync(ForgeProgramme programme) {
            var row = db.ForgeProgrammes.Local.SingleOrDefault(x => x.OwnerScope == programme.OwnerScope && x.Id == programme.Id) ??
                await db.ForgeProgrammes.SingleOrDefaultAsync(x => x.OwnerScope == programme.OwnerScope && x.Id == programme.Id, ct);
            if (row is null) { row = new() { OwnerScope = programme.OwnerScope, Id = programme.Id }; db.ForgeProgrammes.Add(row); }
            row.Apply(programme);
            if (programme.DeletedAt is not null) {
                // A prior session write may already have flushed an appended revision inside this transaction.
                // Detach both staged and flushed immutable rows; SQL below performs the single physical purge.
                // Shared CLR shape: Entries<ForgeProtectedRow>() intentionally spans BOTH named tables here.
                foreach (var entry in db.ChangeTracker.Entries<ForgeProtectedRow>().Where(x =>
                    x.Entity.OwnerScope == programme.OwnerScope && x.Entity.ProgrammeId == programme.Id).ToList()) entry.State = EntityState.Detached;
                foreach (var rows in new[] { db.ForgeCheckIns, db.ForgeCapacity }) {
                    await rows.Where(x => x.OwnerScope == programme.OwnerScope && x.ProgrammeId == programme.Id).ExecuteDeleteAsync(ct);
                }
            }
        }
        private DbSet<ForgeProtectedRow> ProtectedRows(ForgeProtectedKind kind) => kind switch {
            ForgeProtectedKind.CheckIn => db.ForgeCheckIns, ForgeProtectedKind.Capacity => db.ForgeCapacity,
            _ => throw new DomainException(400, "Unknown protected Forge record kind.")
        };
        public async Task<ForgeProtectedRecord?> ReadForgeProtectedRecordAsync(ForgeProtectedKind kind, string ownerScope, Guid programmeId, Guid id) {
            if (await ReadForgeProgrammeAsync(ownerScope, programmeId) is not { DeletedAt: null }) return null;
            var rows = ProtectedRows(kind);
            var staged = LocalProtectedRows(rows).Where(x => x.OwnerScope == ownerScope && x.ProgrammeId == programmeId && x.Id == id).OrderByDescending(x => x.Revision).FirstOrDefault();
            var saved = await rows.AsNoTracking().Where(x => x.OwnerScope == ownerScope && x.ProgrammeId == programmeId && x.Id == id)
                .OrderByDescending(x => x.Revision).FirstOrDefaultAsync(ct);
            return (staged is not null && (saved is null || staged.Revision > saved.Revision) ? staged : saved)?.Record(kind);
        }
        public async Task<ForgeProtectedRecord?> ReadForgeProtectedRequestAsync(ForgeProtectedKind kind, string ownerScope, Guid programmeId, Guid requestId) {
            if (await ReadForgeProgrammeAsync(ownerScope, programmeId) is not { DeletedAt: null }) return null;
            var rows = ProtectedRows(kind);
            return (LocalProtectedRows(rows).SingleOrDefault(x => x.OwnerScope == ownerScope && x.ProgrammeId == programmeId && x.RequestId == requestId) ??
                await rows.AsNoTracking().SingleOrDefaultAsync(x => x.OwnerScope == ownerScope && x.ProgrammeId == programmeId && x.RequestId == requestId, ct))?.Record(kind);
        }
        // DbSet.Local includes rows of the shared CLR type; discriminate the named entity metadata.
        private IEnumerable<ForgeProtectedRow> LocalProtectedRows(DbSet<ForgeProtectedRow> rows) =>
            db.ChangeTracker.Entries<ForgeProtectedRow>().Where(x => x.Metadata == rows.EntityType && x.State != EntityState.Deleted).Select(x => x.Entity);
        public async Task AppendForgeProtectedRecordAsync(ForgeProtectedRecord record) {
            record.Validate(epoch);
            Guard.Require(await ReadForgeProgrammeAsync(record.OwnerScope, record.ProgrammeId) is { DeletedAt: null }, 404, "Forge programme not found.");
            var latest = await ReadForgeProtectedRecordAsync(record.Kind, record.OwnerScope, record.ProgrammeId, record.Id);
            Guard.Require((latest?.Revision ?? 0) == record.ExpectedRevision, 409, "Protected Forge record changed. Reload before editing.");
            Guard.Require(await ReadForgeProtectedRequestAsync(record.Kind, record.OwnerScope, record.ProgrammeId, record.RequestId) is null,
                409, "Protected Forge request ID is already committed.");
            ProtectedRows(record.Kind).Add(ForgeProtectedRow.From(record));
        }
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
