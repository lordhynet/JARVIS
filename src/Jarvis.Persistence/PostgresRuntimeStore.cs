using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Workflows;
using Jarvis.Orchestrator.Runtime;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Persistence;

// A single JSON document is deliberately simple for a personal assistant. The database
// lock, not a process semaphore, coordinates multiple hosts. Split hot tables as needed.
public sealed class PostgresRuntimeStore(IDbContextFactory<SessionDbContext> factory) : IRuntimeStore
{
    public async Task<AudioLease?> ReadAudioLeaseAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await PostgresPrivacyGate.LockAsync(db, ct);
        var json = await db.Database.SqlQueryRaw<string>("SELECT COALESCE(\"Json\"->>'AudioLease', 'null') AS \"Value\" FROM \"RuntimeDocuments\" WHERE \"Id\"=1").SingleAsync(ct);
        var lease = JsonSerializer.Deserialize<AudioLease>(json);
        await tx.CommitAsync(ct); return lease;
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
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await PostgresPrivacyGate.LockAsync(db, ct);
        var row = await db.RuntimeDocuments.AsNoTracking().SingleAsync(r => r.Id == 1, ct);
        var result = read(JsonSerializer.Deserialize<RuntimeState>(row.Json)!);
        await tx.CommitAsync(ct); return result;
    }

    public Task<UsageReservation> SettleUsageAsync(Guid id, decimal actual, int input, int output, CancellationToken ct = default) =>
        UpdateCoreAsync(s => UsageAccounting.Settle(s, id, actual, input, output), true, ct);

    public Task<T> UpdateAsync<T>(Func<RuntimeState, T> update, CancellationToken ct = default) => UpdateCoreAsync(update, false, ct);

    private async Task<T> UpdateCoreAsync<T>(Func<RuntimeState, T> update, bool accountingOnly, CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await PostgresPrivacyGate.LockAsync(db, ct, accountingOnly);
        var rows = await db.RuntimeDocuments.FromSqlRaw(
            "SELECT * FROM \"RuntimeDocuments\" WHERE \"Id\" = 1 FOR UPDATE").ToListAsync(ct);
        var row = rows.Single();
        CoreTelemetry.RuntimeLockWait.Record(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds);
        CoreTelemetry.RuntimeBytes.Record(System.Text.Encoding.UTF8.GetByteCount(row.Json));
        var state = JsonSerializer.Deserialize<RuntimeState>(row.Json)!;
        var before = JsonSerializer.Serialize(state);
        var result = update(state);
        var after = JsonSerializer.Serialize(state);
        if (before != after) { row.Json = after; await db.SaveChangesAsync(ct); CoreTelemetry.RuntimeWrites.Add(1); }
        else CoreTelemetry.RuntimeNoOpWrites.Add(1);
        await tx.CommitAsync(ct);
        return result;
    }
}

public sealed class RuntimeDocument
{
    public int Id { get; set; }
    public string Json { get; set; } = "{}";
}
