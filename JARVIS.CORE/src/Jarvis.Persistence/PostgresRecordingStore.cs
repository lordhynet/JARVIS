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
    public async Task<T> ReadWithRuntimeAsync<T>(Func<RecordingState, RuntimeState, T> read, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct); await PostgresPrivacyGate.LockAsync(db, ct);
        var row = await db.RuntimeDocuments.AsNoTracking().Where(r => r.Id == 1).Select(r => new RuntimeDocument { Id = r.Id, Fingerprint = r.Fingerprint }).SingleAsync(ct);
        var runtime = new RuntimeState(); await PostgresRuntimeHotState.LoadAsync(db, runtime, row.Fingerprint, ct, deferCold: true);
        var result = read(await Snapshot(db, ct), runtime); PostgresRuntimeHotState.Detach(runtime, result);
        await tx.CommitAsync(ct); return result;
    }
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
    public Task<T> UpdateAsync<T>(Func<RecordingState, RuntimeState, Task<T>> update, CancellationToken ct = default) =>
        MutateAsync<object?, T>(null, null, (state, runtime, _) => update(state, runtime), null, ct);
    public Task<T> UpdatePreparedAsync<TPlan, T>(Func<RecordingState, RuntimeState, TPlan> plan,
        Func<TPlan, CancellationToken, Task> prepare, Func<RecordingState, RuntimeState, TPlan, Task<T>> update,
        Func<TPlan, RecordingCommitOutcome, Task>? finish = null, CancellationToken ct = default) => MutateAsync(plan, prepare, update, finish, ct);
    private async Task<T> MutateAsync<TPlan, T>(Func<RecordingState, RuntimeState, TPlan>? plan,
        Func<TPlan, CancellationToken, Task>? prepare, Func<RecordingState, RuntimeState, TPlan, Task<T>> update,
        Func<TPlan, RecordingCommitOutcome, Task>? finish, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct); await using var tx = await db.Database.BeginTransactionAsync(ct);
        TPlan prepared = default!; var planned = false; var committing = false; var outcome = RecordingCommitOutcome.Rejected;
        try {
        await PostgresPrivacyGate.LockAsync(db, ct);
        // Separate from D2: serialize recording file admission across hosts, while voice
        // remains free to renew its lease during encryption, directory IO and fsync.
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(1246906966, 1380270927)", ct);
        // The recording gate excludes every recording writer, and the shared privacy
        // gate excludes erasure/retention. Hydrate and fingerprint these stable rows
        // before D2, rather than making voice wait for a growing chunk collection.
        var state = await Snapshot(db, ct);
        var recordings = state.Sessions.ToDictionary(p => p.Key, p => JsonSerializer.Serialize(p.Value));
        var chunks = state.Chunks.ToDictionary(p => p.Key, p => JsonSerializer.Serialize(p.Value));
        var chunkRecordings = state.Chunks.ToDictionary(p => p.Key, p => p.Value.RecordingId);
        if (plan is not null) {
            var row = await db.RuntimeDocuments.AsNoTracking().Where(r => r.Id == 1).Select(r => new RuntimeDocument { Id = r.Id, Fingerprint = r.Fingerprint }).SingleAsync(ct);
            var runtime = new RuntimeState(); await PostgresRuntimeHotState.LoadAsync(db, runtime, row.Fingerprint, ct, deferCold: true);
            prepared = plan(state, runtime); planned = true;
            PostgresRuntimeHotState.Detach(runtime, prepared);
        }
        // Reload hot runtime rows after staging: a lease or credential may change
        // during file IO. Recording rows are persisted explicitly as a delta below.
        db.ChangeTracker.Clear();
        if (plan is not null) {
            await prepare!(prepared, ct);
        }
        var at = System.Diagnostics.Stopwatch.GetTimestamp();
        var sessions = new List<(Jarvis.Contracts.ConversationSession Before, Jarvis.Contracts.ConversationSession After)>();
        var result = await PostgresRuntimeStore.UpdateInTransactionAsync(db, async runtime => {
            state.UpdateSession = new PostgresCoreTransaction.Writes(db, runtime, sessions, ct).UpdateSessionAsync;
            var value = await update(state, runtime, prepared);
            foreach (var id in recordings.Keys.Except(state.Sessions.Keys)) db.InterviewRecordings.Remove(new() { Id = id });
            foreach (var (id, session) in state.Sessions) {
                var json = JsonSerializer.Serialize(session); if (recordings.GetValueOrDefault(id) == json) continue;
                var row = new InterviewRecordingRow { Id = id, Json = json };
                if (recordings.ContainsKey(id)) db.InterviewRecordings.Update(row); else db.InterviewRecordings.Add(row);
            }
            // Parent deletion already cascades in PostgreSQL. Do not also issue
            // detached child deletes after that cascade (which would affect zero rows).
            foreach (var id in chunks.Keys.Except(state.Chunks.Keys).Where(id => state.Sessions.ContainsKey(chunkRecordings[id])))
                db.RecordingChunks.Remove(new() { Id = id, RecordingId = chunkRecordings[id] });
            foreach (var (id, chunk) in state.Chunks) {
                var json = JsonSerializer.Serialize(chunk); if (chunks.GetValueOrDefault(id) == json) continue;
                var row = new RecordingChunkRow { Id = id, RecordingId = chunk.RecordingId, Track = chunk.Track, Sequence = chunk.Sequence, Json = json };
                if (chunks.ContainsKey(id)) db.RecordingChunks.Update(row); else db.RecordingChunks.Add(row);
            }
            return value;
        }, at, at, ct);
        committing = true; await tx.CommitAsync(ct); outcome = RecordingCommitOutcome.Committed;
        audioChanges.LeaseChanged(result.Before, result.After);
        foreach (var (before, after) in sessions) audioChanges.SessionChanged(before, after);
        return result.Result;
        } catch {
            if (committing && outcome != RecordingCommitOutcome.Committed) outcome = RecordingCommitOutcome.Unknown;
            try { await tx.RollbackAsync(CancellationToken.None); } catch { }
            throw;
        } finally {
            // A known rejection can remove its unique staged blob after rollback releases
            // D2. An uncertain commit retains it: metadata may already reference it.
            if (planned && finish is not null) try { await finish(prepared, outcome); } catch when (outcome != RecordingCommitOutcome.Committed) { }
        }
    }
}
