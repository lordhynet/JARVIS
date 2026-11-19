using System.Text.Json;
using Jarvis.Orchestrator.Recordings;
using Jarvis.Orchestrator.Runtime;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Persistence;

public sealed class InterviewRecordingRow { public Guid Id { get; set; } public string Json { get; set; } = "{}"; }
public sealed class RecordingChunkRow
{
    public Guid Id { get; set; } public Guid RecordingId { get; set; } public string Track { get; set; } = ""; public int Sequence { get; set; }
    public string Json { get; set; } = "{}";
}
internal static class RecordingModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<InterviewRecordingRow>(b => { b.ToTable("InterviewRecordings"); b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever(); b.Property(x => x.Json).HasColumnType("jsonb"); });
        model.Entity<RecordingChunkRow>(b => { b.ToTable("RecordingChunks"); b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever(); b.Property(x => x.Json).HasColumnType("jsonb");
            b.Property(x => x.Track).HasMaxLength(16); b.HasIndex(x => new { x.RecordingId, x.Track, x.Sequence }).IsUnique(); b.HasOne<InterviewRecordingRow>().WithMany().HasForeignKey(x => x.RecordingId).OnDelete(DeleteBehavior.Cascade); });
    }
}
public sealed class PostgresRecordingStore(IDbContextFactory<SessionDbContext> factory, AudioFenceChanges audioChanges) : IRecordingStore
{
    internal static async Task<RecordingState> Snapshot(SessionDbContext db, CancellationToken ct) => new() {
        Epoch = await db.PrivacyControls.Select(p => p.Epoch).SingleAsync(ct),
        Sessions = (await db.InterviewRecordings.ToListAsync(ct)).ToDictionary(x => x.Id, x => JsonSerializer.Deserialize<RecordingSession>(x.Json)!),
        Chunks = (await db.RecordingChunks.ToListAsync(ct)).ToDictionary(x => x.Id, x => JsonSerializer.Deserialize<RecordingChunk>(x.Json)!)
    };
    public async Task<T> ReadAsync<T>(Func<RecordingState, T> read, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await PostgresPrivacyGate.LockAsync(db, ct); var result = read(await Snapshot(db, ct)); await tx.CommitAsync(ct); return result;
    }
    public async Task<T> UpdateAsync<T>(Func<RecordingState, RuntimeState, Task<T>> update, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct); await PostgresPrivacyGate.LockAsync(db, ct);
        var at = System.Diagnostics.Stopwatch.GetTimestamp();
        var result = await PostgresRuntimeStore.UpdateInTransactionAsync(db, async runtime => {
            var state = await Snapshot(db, ct); var recordings = state.Sessions.ToDictionary(p => p.Key, p => JsonSerializer.Serialize(p.Value));
            var chunks = state.Chunks.ToDictionary(p => p.Key, p => JsonSerializer.Serialize(p.Value)); var value = await update(state, runtime);
            foreach (var row in db.InterviewRecordings.Local.ToArray()) if (!state.Sessions.ContainsKey(row.Id)) db.InterviewRecordings.Remove(row);
            foreach (var (id, session) in state.Sessions) {
                var json = JsonSerializer.Serialize(session); if (recordings.GetValueOrDefault(id) == json) continue;
                var row = db.InterviewRecordings.Local.SingleOrDefault(x => x.Id == id); if (row is null) db.InterviewRecordings.Add(new() { Id = id, Json = json }); else row.Json = json;
            }
            foreach (var row in db.RecordingChunks.Local.ToArray()) if (!state.Chunks.ContainsKey(row.Id)) db.RecordingChunks.Remove(row);
            foreach (var (id, chunk) in state.Chunks) {
                var json = JsonSerializer.Serialize(chunk); if (chunks.GetValueOrDefault(id) == json) continue;
                var row = db.RecordingChunks.Local.SingleOrDefault(x => x.Id == id);
                if (row is null) db.RecordingChunks.Add(new() { Id = id, RecordingId = chunk.RecordingId, Track = chunk.Track, Sequence = chunk.Sequence, Json = json }); else row.Json = json;
            }
            return value;
        }, at, at, ct);
        await tx.CommitAsync(ct); audioChanges.LeaseChanged(result.Before, result.After); return result.Result;
    }
}
