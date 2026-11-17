using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Workflows;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Jarvis.Persistence;

public sealed class PostgresWorkflowStore(IDbContextFactory<SessionDbContext> factory) : IWorkflowStore
{
    public async Task<WorkflowRun> AddAsync(WorkflowRun candidate, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await PostgresPrivacyGate.LockAsync(db, ct);
        db.WorkflowRuns.Add(WorkflowRunRow.From(candidate));
        db.WorkflowEvents.Add(Event(candidate));
        try { await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return candidate; }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await tx.RollbackAsync(ct);
            // A competing host may have committed the same trigger. Only an existing run
            // with this exact ID is a retry; unrelated constraint failures must propagate.
            // Recheck the data boundary after rolling back the failed insert transaction.
            await using var retry = await db.Database.BeginTransactionAsync(ct);
            await PostgresPrivacyGate.LockAsync(db, ct);
            var existing = await db.WorkflowRuns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == candidate.Id, ct);
            if (existing is null) throw;
            await retry.CommitAsync(ct);
            return existing.ToContract();
        }
    }

    public async Task<WorkflowRun?> ClaimAsync(DateTimeOffset now, Func<WorkflowRun, WorkflowRun> claim, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await PostgresPrivacyGate.LockAsync(db, ct);
        var rows = await db.WorkflowRuns.FromSqlInterpolated($"""
            SELECT * FROM "WorkflowRuns"
            WHERE "AvailableAt" <= {now}
            ORDER BY "AvailableAt", "Id"
            LIMIT 1 FOR UPDATE SKIP LOCKED
            """).ToListAsync(ct);
        if (rows.Count == 0) return null;
        var row = rows[0];
        var updated = claim(row.ToContract());
        row.Apply(updated);
        db.WorkflowEvents.Add(Event(updated));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return updated;
    }

    public async Task<WorkflowRun?> UpdateAsync(Guid id, Func<WorkflowRun, WorkflowRun> update, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await PostgresPrivacyGate.LockAsync(db, ct);
        var rows = await db.WorkflowRuns.FromSqlInterpolated($"""
            SELECT * FROM "WorkflowRuns" WHERE "Id" = {id} FOR UPDATE
            """).ToListAsync(ct);
        if (rows.Count == 0) return null;
        var row = rows[0];
        var original = row.ToContract();
        var updated = update(original);
        if (original.Revision != updated.Revision)
        {
            row.Apply(updated);
            db.WorkflowEvents.Add(Event(updated));
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
        return updated;
    }

    public async Task<WorkflowRun?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct); await PostgresPrivacyGate.LockAsync(db, ct);
        var result = (await db.WorkflowRuns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct))?.ToContract();
        await tx.CommitAsync(ct); return result;
    }

    public async Task<IReadOnlyList<WorkflowRun>> ListAsync(CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct); await PostgresPrivacyGate.LockAsync(db, ct);
        var rows = await db.WorkflowRuns.FromSqlRaw("SELECT * FROM \"WorkflowRuns\" WHERE \"Json\" ->> 'DeletedAt' IS NULL")
            .AsNoTracking().OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Take(25).ToListAsync(ct);
        var result = rows.Select(x => x.ToContract()).ToList(); await tx.CommitAsync(ct); return result;
    }

    public async Task<IReadOnlyList<WorkflowEvent>> EventsAsync(Guid id, int afterRevision, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct); await PostgresPrivacyGate.LockAsync(db, ct);
        var result = await db.WorkflowEvents.AsNoTracking().Where(x => x.RunId == id && x.Revision > afterRevision)
            .OrderBy(x => x.Revision).Take(100)
            .Select(x => new WorkflowEvent(x.EventId, x.RunId, x.Revision, x.Status, x.OccurredAt)).ToListAsync(ct);
        await tx.CommitAsync(ct); return result;
    }

    private static WorkflowEventRow Event(WorkflowRun run) => new()
    {
        EventId = Guid.NewGuid(), RunId = run.Id, Revision = run.Revision,
        Status = run.Status, OccurredAt = run.UpdatedAt
    };
}

public sealed class WorkflowRunRow
{
    public Guid Id { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? AvailableAt { get; set; }
    public string Json { get; set; } = "{}";
    public WorkflowRun ToContract() => JsonSerializer.Deserialize<WorkflowRun>(Json)!;
    public void Apply(WorkflowRun run)
    {
        AvailableAt = run.AvailableAt;
        Json = JsonSerializer.Serialize(run);
    }
    public static WorkflowRunRow From(WorkflowRun run)
    {
        var row = new WorkflowRunRow { Id = run.Id, CreatedAt = run.CreatedAt };
        row.Apply(run);
        return row;
    }
}

public sealed class WorkflowEventRow
{
    public Guid EventId { get; set; }
    public Guid RunId { get; set; }
    public int Revision { get; set; }
    public WorkflowStatus Status { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
}
