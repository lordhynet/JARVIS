using Jarvis.Orchestrator.Cqrs.Integrations;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Persistence;

public sealed class PostgresReadiness(IDbContextFactory<SessionDbContext> factory) : IStorageReadiness
{
    public async Task<StorageReadiness> CheckAsync(CancellationToken ct)
    {
        try
        {
            await using var db = await factory.CreateDbContextAsync(ct);
            return new(await db.Database.CanConnectAsync(ct), "Postgres");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(false, "Postgres");
        }
    }
}
