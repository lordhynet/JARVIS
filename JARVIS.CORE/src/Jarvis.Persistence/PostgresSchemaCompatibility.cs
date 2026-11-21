using System.Data;
using Jarvis.Orchestrator.Runtime;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Persistence;

// Uses the same session-level lock as the designated migrator, before workers start.
public static class PostgresSchemaCompatibility
{
    public static Task RequireCurrentAsync(SessionDbContext db, CancellationToken ct = default) =>
        WithMigrationLock(db, async () => {
            var (known, applied) = await KnownHistory(db, ct);
            RequireAllApplied(known, applied);
        }, ct);

    public static Task MigrateAsync(SessionDbContext db, CancellationToken ct = default) =>
        WithMigrationLock(db, async () => {
            _ = await KnownHistory(db, ct); // Never mutate a schema belonging to a newer/different assembly.
            // Count other clients, including idle/untagged legacy hosts. Never kill clients.
            var others = await db.Database.SqlQueryRaw<int>("""
                SELECT count(*)::integer AS "Value" FROM pg_stat_activity
                WHERE datname = current_database() AND backend_type = 'client backend' AND pid <> pg_backend_pid()
                """).SingleAsync(ct);
            Guard.Require(others == 0, 503, "Stop all other clients connected to this database, including starting Core hosts and concurrent migrators, before running the designated migrator.", "schema_other_sessions");
            await db.Database.MigrateAsync(ct);
            var (known, applied) = await KnownHistory(db, ct);
            RequireAllApplied(known, applied);
        }, ct);

    private static async Task<(string[] Known, string[] Applied)> KnownHistory(SessionDbContext db, CancellationToken ct)
    {
        var known = db.Database.GetMigrations().ToArray();
        var applied = (await db.Database.GetAppliedMigrationsAsync(ct)).ToArray();
        Guard.Require(!applied.Except(known, StringComparer.Ordinal).Any(), 503,
            "This database contains migrations unknown to this Core build. Use a compatible build. " + History(known, applied), "schema_unknown_migration");
        return (known, applied);
    }

    private static void RequireAllApplied(string[] known, string[] applied) =>
        Guard.Require(!known.Except(applied, StringComparer.Ordinal).Any(), 503,
            "Run the designated migrator before starting this Core host. " + History(known, applied), "schema_pending_migration");

    // Migration identifiers only; never include connection strings or configuration.
    private static string History(string[] known, string[] applied) =>
        $"Database applied migrations: [{string.Join(", ", applied)}]. Build known migrations: [{string.Join(", ", known)}].";

    private static async Task WithMigrationLock(SessionDbContext db, Func<Task> action, CancellationToken ct)
    {
        var openedHere = db.Database.GetDbConnection().State != ConnectionState.Open;
        if (openedHere) await db.Database.OpenConnectionAsync(ct);
        try {
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_lock(746271590)", ct);
            try { await action(); }
            // Release even when the operation's cancellation token is cancelled.
            finally { await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock(746271590)", CancellationToken.None); }
        }
        finally { if (openedHere) await db.Database.CloseConnectionAsync(); }
    }
}
