using System.Diagnostics;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Workflows;
using Jarvis.Orchestrator.Runtime;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Persistence;

// The cold document lock coordinates the D2 transaction across hosts. Hot audio, ticket
// and transcript rows share this boundary without rewriting the cold document.
public sealed class PostgresRuntimeStore(IDbContextFactory<SessionDbContext> factory, AudioFenceChanges? audioChanges = null) : IRuntimeStore
{
    public async Task<AudioLease?> ReadAudioLeaseAsync(CancellationToken ct = default)
    {
        var phase = Stopwatch.GetTimestamp();
        await using var db = await factory.CreateDbContextAsync(ct);
        phase = CoreTelemetry.RecordStoragePhase(phase, "lease.context");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        phase = CoreTelemetry.RecordStoragePhase(phase, "lease.begin");
        await PostgresPrivacyGate.LockAsync(db, ct);
        phase = CoreTelemetry.RecordStoragePhase(phase, "lease.privacy_gate");
        var json = await db.Database.SqlQueryRaw<string>("SELECT \"LeaseJson\"::text AS \"Value\" FROM \"RuntimeAudioState\" WHERE \"Id\"=1").SingleAsync(ct);
        phase = CoreTelemetry.RecordStoragePhase(phase, "lease.load");
        var lease = JsonSerializer.Deserialize<AudioLease>(json);
        phase = CoreTelemetry.RecordStoragePhase(phase, "lease.deserialize");
        await tx.CommitAsync(ct);
        CoreTelemetry.RecordStoragePhase(phase, "lease.commit");
        return lease;
    }
    public async Task<AudioLeaseState> ReadAudioLeaseStateAsync(CancellationToken ct = default)
    {
        var json = await ReadProjectionAsync($"""
            SELECT jsonb_build_object('Lease', "LeaseJson", 'DrainUntil', "DrainUntil")::text AS "Value"
            FROM "RuntimeAudioState" WHERE "Id"=1
            """, ct);
        return JsonSerializer.Deserialize<AudioLeaseState>(json);
    }

    public async Task<RegisteredDevice?> ReadRegisteredDeviceAsync(string id, CancellationToken ct = default)
    {
        var json = await ReadProjectionAsync($"""
            SELECT COALESCE("DevicesJson"->>(CAST({id} AS text)), 'null') AS "Value"
            FROM "RuntimeAudioState" WHERE "Id"=1
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
                ("ControlsJson"->>'PaidAiPaused')::boolean,
                ("ControlsJson"->>'Paused')::boolean,
                false) AS "Value"
            FROM "RuntimeAudioState" WHERE "Id"=1
            """).SingleAsync(ct);
        await tx.CommitAsync(ct);
        return paused;
    }
    public async Task<T> ReadAsync<T>(Func<RuntimeState, T> read, CancellationToken ct = default)
    {
        var phase = Stopwatch.GetTimestamp();
        await using var db = await factory.CreateDbContextAsync(ct);
        phase = CoreTelemetry.RecordStoragePhase(phase, "runtime.read.context");
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        phase = CoreTelemetry.RecordStoragePhase(phase, "runtime.read.begin");
        await PostgresPrivacyGate.LockAsync(db, ct);
        phase = CoreTelemetry.RecordStoragePhase(phase, "runtime.read.privacy_gate");
        var row = await db.RuntimeDocuments.AsNoTracking().Where(r => r.Id == 1)
            .Select(r => new RuntimeDocument { Id = r.Id, Fingerprint = r.Fingerprint }).SingleAsync(ct);
        phase = CoreTelemetry.RecordStoragePhase(phase, "runtime.read.load");
        var state = new RuntimeState();
        await PostgresRuntimeHotState.LoadAsync(db, state, row.Fingerprint, ct, deferCold: true);
        phase = CoreTelemetry.RecordStoragePhase(phase, "runtime.read.deserialize");
        var result = read(state);
        PostgresRuntimeHotState.Detach(state, result);
        phase = CoreTelemetry.RecordStoragePhase(phase, "runtime.read.callback");
        await tx.CommitAsync(ct);
        CoreTelemetry.RecordStoragePhase(phase, "runtime.read.commit");
        return result;
    }

    public Task<UsageReservation> SettleUsageAsync(Guid id, decimal actual, int input, int output, CancellationToken ct = default,
        DateTimeOffset? completedAt = null) =>
        UpdateCoreAsync(s => UsageAccounting.Settle(s, id, actual, input, output, completedAt), true, ct);

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
        var rows = await db.Database.SqlQueryRaw<RuntimeDocumentStamp>(
            "SELECT \"Id\", \"Fingerprint\" FROM \"RuntimeDocuments\" WHERE \"Id\" = 1 FOR UPDATE").ToListAsync(ct);
        var stamp = rows.Single();
        var row = new RuntimeDocument { Id = stamp.Id, Fingerprint = stamp.Fingerprint };
        db.RuntimeDocuments.Attach(row);
        phase = CoreTelemetry.RecordStoragePhase(phase, "runtime.update.lock_and_load");
        CoreTelemetry.RuntimeLockWait.Record(Stopwatch.GetElapsedTime(started, phase).TotalSeconds);
        var state = new RuntimeState();
        var hot = await PostgresRuntimeHotState.LoadAsync(db, state, row.Fingerprint, ct, deferCold: true);
        phase = CoreTelemetry.RecordStoragePhase(phase, "runtime.update.deserialize");
        var previousLease = state.AudioLease;
        var result = await update(state);
        phase = CoreTelemetry.RecordStoragePhase(phase, "runtime.update.callback");
        hot.Apply(row, state);
        PostgresRuntimeHotState.Detach(state, result);
        phase = CoreTelemetry.RecordStoragePhase(phase, "runtime.update.serialize_after");
        if (db.ChangeTracker.HasChanges()) { await db.SaveChangesAsync(ct); CoreTelemetry.RuntimeWrites.Add(1); }
        else CoreTelemetry.RuntimeNoOpWrites.Add(1);
        phase = CoreTelemetry.RecordStoragePhase(phase, "runtime.update.save");
        return (result, previousLease, state.AudioLease, phase);
    }
}

internal sealed record RuntimeDocumentStamp(int Id, string Fingerprint);

public sealed class RuntimeDocument
{
    public int Id { get; set; }
    public string Json { get; set; } = "{}";
    public string Fingerprint { get; set; } = "";
}
