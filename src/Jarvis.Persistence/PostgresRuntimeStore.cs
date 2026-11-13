using System.Text.Json;
using Jarvis.Orchestrator.Runtime;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Persistence;

// A single JSON document is deliberately simple for a personal assistant. The database
// lock, not a process semaphore, coordinates multiple hosts. Split hot tables as needed.
public sealed class PostgresRuntimeStore(IDbContextFactory<SessionDbContext> factory) : IRuntimeStore
{
    public async Task<T> ReadAsync<T>(Func<RuntimeState, T> read, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var row = await db.RuntimeDocuments.AsNoTracking().SingleAsync(r => r.Id == 1, ct);
        return read(JsonSerializer.Deserialize<RuntimeState>(row.Json)!);
    }

    public async Task<T> UpdateAsync<T>(Func<RuntimeState, T> update, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var rows = await db.RuntimeDocuments.FromSqlRaw(
            "SELECT * FROM \"RuntimeDocuments\" WHERE \"Id\" = 1 FOR UPDATE").ToListAsync(ct);
        var row = rows.Single();
        var state = JsonSerializer.Deserialize<RuntimeState>(row.Json)!;
        var result = update(state);
        row.Json = JsonSerializer.Serialize(state);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return result;
    }
}

public sealed class RuntimeDocument
{
    public int Id { get; set; }
    public string Json { get; set; } = "{}";
}
