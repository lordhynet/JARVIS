using System.Diagnostics;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Workflows;
using Jarvis.Orchestrator.Runtime;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Persistence;

// The database locks coordinate runtime updates across Core processes.
public sealed class PostgresRuntimeStore(IDbContextFactory<SessionDbContext> factory, AudioFenceChanges? audioChanges = null) : IRuntimeStore
{
    public async Task<AudioLease?> ReadAudioLeaseAsync(CancellationToken ct = default) => (await ReadAudioStateAsync(ct)).Lease;

    public async Task<AudioLeaseState> ReadAudioLeaseStateAsync(CancellationToken ct = default)
    {
        var audio = await ReadAudioStateAsync(ct);
        return new(audio.Lease, audio.DrainUntil);
    }

    private async Task<RuntimeAudioLeaseState> ReadAudioStateAsync(CancellationToken ct)
    {
        var phase = Stopwatch.GetTimestamp();
        await using var db = await factory.CreateDbContextAsync(ct);
        phase = CoreTelemetry.RecordStoragePhase(phase, "lease.context");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        phase = CoreTelemetry.RecordStoragePhase(phase, "lease.begin");
        await PostgresPrivacyGate.LockAsync(db, ct);
        phase = CoreTelemetry.RecordStoragePhase(phase, "lease.privacy_gate");
        var row = await db.RuntimeAudioStates.AsNoTracking().SingleAsync(r => r.Id == 1, ct);
        phase = CoreTelemetry.RecordStoragePhase(phase, "lease.load");
        var audio = row.ToState();
        await tx.CommitAsync(ct);
        CoreTelemetry.RecordStoragePhase(phase, "lease.commit");
        return audio;
    }

    public async Task<bool> ReleaseAudioAsync(AudioLease lease, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await PostgresPrivacyGate.LockAsync(db, ct);
        var row = await RuntimeAudioRow.LockAsync(db, ct);
        var before = row.ToState();
        var after = before.Release(lease);
        if (before == after)
        {
            CoreTelemetry.RuntimeNoOpWrites.Add(1);
            await tx.CommitAsync(ct);
            return false;
        }
        row.Apply(after);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        CoreTelemetry.RuntimeWrites.Add(1);
        audioChanges?.LeaseChanged(before.Lease, after.Lease);
        return true;
    }

    public async Task<RegisteredDevice?> ReadRegisteredDeviceAsync(string id, CancellationToken ct = default)
    {
        var json = await ReadProjectionAsync($"""
            SELECT COALESCE("Json"->'Devices'->>(CAST({id} AS text)), 'null') AS "Value"
            FROM "RuntimeDocuments" WHERE "Id"=1
            """, ct);
        return JsonSerializer.Deserialize<RegisteredDevice>(json);
    }

    private async Task<string> ReadProjectionAsync(FormattableString query, CancellationToken ct)
    {
        var phase = Stopwatch.GetTimestamp();
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await PostgresPrivacyGate.LockAsync(db, ct);
        phase = CoreTelemetry.RecordStoragePhase(phase, "runtime.projection.begin_and_gate");
        var value = await db.Database.SqlQuery<string>(query).SingleAsync(ct);
        phase = CoreTelemetry.RecordStoragePhase(phase, "runtime.projection.load");
        await tx.CommitAsync(ct);
        CoreTelemetry.RecordStoragePhase(phase, "runtime.projection.commit");
        return value;
    }

    public async Task<bool> ReadPaidAiPausedAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await PostgresPrivacyGate.LockAsync(db, ct);
        var paused = await db.Database.SqlQueryRaw<bool>("""
            SELECT COALESCE(
                ("Json"->'AgentControls'->>'PaidAiPaused')::boolean,
                ("Json"->'AgentControls'->>'Paused')::boolean,
                false) AS "Value"
            FROM "RuntimeDocuments" WHERE "Id"=1
            """).SingleAsync(ct);
        await tx.CommitAsync(ct);
        return paused;
    }
    public async Task<T> ReadAsync<T>(Func<RuntimeState, T> read, CancellationToken ct = default)
    {
        var phase = Stopwatch.GetTimestamp();
        await using var db = await factory.CreateDbContextAsync(ct);
        phase = CoreTelemetry.RecordStoragePhase(phase, "runtime.read.context");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        phase = CoreTelemetry.RecordStoragePhase(phase, "runtime.read.begin");
        await PostgresPrivacyGate.LockAsync(db, ct);
        phase = CoreTelemetry.RecordStoragePhase(phase, "runtime.read.privacy_gate");
        var row = await (from document in db.RuntimeDocuments.AsNoTracking()
                         from audio in db.RuntimeAudioStates.AsNoTracking()
                         where document.Id == 1 && audio.Id == 1
                         select new { document.Json, Audio = audio }).SingleAsync(ct);
        phase = CoreTelemetry.RecordStoragePhase(phase, "runtime.read.load");
        var state = JsonSerializer.Deserialize<RuntimeState>(row.Json)!;
        row.Audio.ToState().Apply(state);
        phase = CoreTelemetry.RecordStoragePhase(phase, "runtime.read.deserialize");
        var result = read(state);
        phase = CoreTelemetry.RecordStoragePhase(phase, "runtime.read.callback");
        await tx.CommitAsync(ct);
        CoreTelemetry.RecordStoragePhase(phase, "runtime.read.commit");
        return result;
    }

    public Task<UsageReservation> SettleUsageAsync(Guid id, decimal actual, int input, int output, CancellationToken ct = default) =>
        UpdateCoreAsync(s => UsageAccounting.Settle(s, id, actual, input, output), true, ct);

    public Task<T> UpdateAsync<T>(Func<RuntimeState, T> update, CancellationToken ct = default) => UpdateCoreAsync(update, false, ct);

    private async Task<T> UpdateCoreAsync<T>(Func<RuntimeState, T> update, bool accountingOnly, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        await using var db = await factory.CreateDbContextAsync(ct);
        var phase = CoreTelemetry.RecordStoragePhase(started, "runtime.update.context");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        phase = CoreTelemetry.RecordStoragePhase(phase, "runtime.update.begin");
        await PostgresPrivacyGate.LockAsync(db, ct, accountingOnly);
        phase = CoreTelemetry.RecordStoragePhase(phase, "runtime.update.privacy_gate");
        var updateResult = await UpdateInTransactionAsync(db, state => Task.FromResult(update(state)), started, phase, ct);
        await tx.CommitAsync(ct);
        audioChanges?.LeaseChanged(updateResult.Before, updateResult.After);
        CoreTelemetry.RecordStoragePhase(updateResult.SavedAt, "runtime.update.commit");
        return updateResult.Result;
    }

    internal static async Task<(T Result, AudioLease? Before, AudioLease? After, long SavedAt)> UpdateInTransactionAsync<T>(
        SessionDbContext db, Func<RuntimeState, Task<T>> update, long started, long phase, CancellationToken ct)
    {
        var rows = await db.RuntimeDocuments.FromSqlRaw(
            "SELECT * FROM \"RuntimeDocuments\" WHERE \"Id\" = 1 FOR UPDATE").ToListAsync(ct);
        var row = rows.Single();
        var audioRow = await RuntimeAudioRow.LockAsync(db, ct);
        phase = CoreTelemetry.RecordStoragePhase(phase, "runtime.update.lock_and_load");
        CoreTelemetry.RuntimeLockWait.Record(Stopwatch.GetElapsedTime(started, phase).TotalSeconds);
        CoreTelemetry.RuntimeBytes.Record(System.Text.Encoding.UTF8.GetByteCount(row.Json));
        var state = JsonSerializer.Deserialize<RuntimeState>(row.Json)!;
        phase = CoreTelemetry.RecordStoragePhase(phase, "runtime.update.deserialize");
        // Keep the migrated audio fields in the document readable for rollback.
        var legacyAudio = RuntimeAudioLeaseState.From(state);
        var before = JsonSerializer.Serialize(state);
        phase = CoreTelemetry.RecordStoragePhase(phase, "runtime.update.serialize_before");
        var beforeAudio = audioRow.ToState();
        beforeAudio.Apply(state);
        var result = await update(state);
        phase = CoreTelemetry.RecordStoragePhase(phase, "runtime.update.callback");
        var afterAudio = RuntimeAudioLeaseState.From(state);
        legacyAudio.Apply(state);
        var after = JsonSerializer.Serialize(state);
        afterAudio.Apply(state);
        phase = CoreTelemetry.RecordStoragePhase(phase, "runtime.update.serialize_after");
        if (before != after) row.Json = after;
        if (beforeAudio != afterAudio) audioRow.Apply(afterAudio);
        if (before != after || beforeAudio != afterAudio)
        {
            await db.SaveChangesAsync(ct);
            CoreTelemetry.RuntimeWrites.Add(1);
        }
        else CoreTelemetry.RuntimeNoOpWrites.Add(1);
        phase = CoreTelemetry.RecordStoragePhase(phase, "runtime.update.save");
        return (result, beforeAudio.Lease, afterAudio.Lease, phase);
    }
}

public sealed class RuntimeDocument
{
    public int Id { get; set; }
    public string Json { get; set; } = "{}";
}
