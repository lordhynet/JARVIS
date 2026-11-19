using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Travel;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Persistence;

public sealed class TravelRouteRow { public Guid Id { get; set; } public string NameKey { get; set; } = ""; public string Json { get; set; } = "{}"; }
public sealed class TravelLineStateRow { public string LineId { get; set; } = ""; public string Json { get; set; } = "{}"; }
public sealed class TravelMonitorRow { public int Id { get; set; } = 1; public string Json { get; set; } = "{}"; }
internal static class TravelModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<TravelRouteRow>(b => { b.ToTable("TravelRoutes"); b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever(); b.Property(x => x.NameKey).HasMaxLength(80); b.HasIndex(x => x.NameKey).IsUnique(); b.Property(x => x.Json).HasColumnType("jsonb"); });
        model.Entity<TravelLineStateRow>(b => { b.ToTable("TravelLineStates"); b.HasKey(x => x.LineId); b.Property(x => x.LineId).HasMaxLength(60); b.Property(x => x.Json).HasColumnType("jsonb"); });
        model.Entity<TravelMonitorRow>(b => { b.ToTable("TravelMonitorState"); b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever(); b.Property(x => x.Json).HasColumnType("jsonb"); b.HasData(new TravelMonitorRow()); });
    }
}
public sealed class PostgresTravelStore(IDbContextFactory<SessionDbContext> factory, AudioFenceChanges audioChanges) : ITravelStore
{
    internal static async Task<TravelState> Snapshot(SessionDbContext db, CancellationToken ct) => new() {
        Routes = (await db.TravelRoutes.ToListAsync(ct)).ToDictionary(x => x.Id, x => JsonSerializer.Deserialize<TravelRoute>(x.Json)!),
        Lines = (await db.TravelLineStates.ToListAsync(ct)).ToDictionary(x => x.LineId, x => JsonSerializer.Deserialize<TravelLineState>(x.Json)!),
        Monitor = JsonSerializer.Deserialize<TravelMonitorState>((await db.TravelMonitorState.SingleAsync(ct)).Json)!
    };
    public async Task<T> ReadAsync<T>(Func<TravelState, T> read, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await PostgresPrivacyGate.LockAsync(db, ct); var result = read(await Snapshot(db, ct)); await tx.CommitAsync(ct); return result;
    }
    public async Task<T> UpdateAsync<T>(Func<TravelState, RuntimeState, T> update, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        await PostgresPrivacyGate.LockAsync(db, ct); var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var result = await PostgresRuntimeStore.UpdateInTransactionAsync(db, async runtime => {
            var state = await Snapshot(db, ct);
            var routes = state.Routes.ToDictionary(p => p.Key, p => JsonSerializer.Serialize(p.Value));
            var lines = state.Lines.ToDictionary(p => p.Key, p => JsonSerializer.Serialize(p.Value));
            var monitor = JsonSerializer.Serialize(state.Monitor); var value = update(state, runtime);
            foreach (var row in db.TravelRoutes.Local.ToArray()) if (!state.Routes.ContainsKey(row.Id)) db.TravelRoutes.Remove(row);
            foreach (var (id, route) in state.Routes) {
                var json = JsonSerializer.Serialize(route); if (routes.GetValueOrDefault(id) == json) continue;
                var row = db.TravelRoutes.Local.SingleOrDefault(x => x.Id == id);
                if (row is null) db.TravelRoutes.Add(new() { Id = id, NameKey = route.Name.ToUpperInvariant(), Json = json });
                else { row.NameKey = route.Name.ToUpperInvariant(); row.Json = json; }
            }
            foreach (var row in db.TravelLineStates.Local.ToArray()) if (!state.Lines.ContainsKey(row.LineId)) db.TravelLineStates.Remove(row);
            foreach (var (id, line) in state.Lines) {
                var json = JsonSerializer.Serialize(line); if (lines.GetValueOrDefault(id) == json) continue;
                var row = db.TravelLineStates.Local.SingleOrDefault(x => x.LineId == id);
                if (row is null) db.TravelLineStates.Add(new() { LineId = id, Json = json }); else row.Json = json;
            }
            var after = JsonSerializer.Serialize(state.Monitor); if (after != monitor) db.TravelMonitorState.Local.Single().Json = after;
            return value;
        }, started, started, ct);
        await tx.CommitAsync(ct); audioChanges.LeaseChanged(result.Before, result.After); return result.Result;
    }
}
