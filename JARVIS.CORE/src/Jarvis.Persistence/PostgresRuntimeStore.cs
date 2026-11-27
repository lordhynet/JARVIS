using System.Diagnostics;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Workflows;
using Jarvis.Orchestrator.Runtime;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Persistence;

// The cold document lock coordinates the D2 transaction across hosts. Hot audio, ticket
// and transcript rows share this boundary without rewriting the cold document.
public sealed partial class PostgresRuntimeStore(IDbContextFactory<SessionDbContext> factory, AudioFenceChanges? audioChanges = null) : IRuntimeStore
{
    public async Task<LearningItem> CreateLearningItemAsync(CreateLearningRequest request, TimeProvider clock, CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        var normalized = LearningService.NormalizeCreate(request);
        _ = LearningService.CreationEpoch(normalized);
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var privacy = await PostgresPrivacyGate.LockAsync(db, ct);
        var stamps = await db.Database.SqlQueryRaw<RuntimeDocumentStamp>(
            "SELECT \"Id\", \"Fingerprint\" FROM \"RuntimeDocuments\" WHERE \"Id\"=1 FOR UPDATE").ToListAsync(ct);
        _ = stamps.Single();
        CoreTelemetry.RuntimeLockWait.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
        LearningCreationReceipt? previous = null;
        if (normalized.RequestId is { } requestId) {
            var row = await db.RuntimeLearningRows.AsNoTracking().SingleOrDefaultAsync(x => x.Kind == nameof(RuntimeState.CreationReceipts) && x.Id == requestId, ct);
            if (row is not null) {
                previous = JsonSerializer.Deserialize<LearningCreationReceipt>(row.Json) ?? throw new InvalidDataException("Null persisted creation receipt.");
                if (previous.RequestId != row.Id) throw new InvalidDataException("Creation receipt identity differs from its row.");
            }
        }
        var change = LearningService.DecideCreate(normalized, clock, privacy.Epoch, previous);
        var item = change.Result;
        if (previous is null) {
            db.RuntimeLearningRows.Add(new() { Kind = nameof(RuntimeState.Learning), Id = item.Id,
                Json = JsonSerializer.Serialize(item), DueAt = item.DueAt.ToUniversalTime() });
            if (change.Receipt is { } receipt) db.RuntimeLearningRows.Add(new() { Kind = nameof(RuntimeState.CreationReceipts),
                Id = receipt.RequestId, Json = JsonSerializer.Serialize(receipt) });
            await db.SaveChangesAsync(ct);
        }
        var phase = Stopwatch.GetTimestamp();
        await tx.CommitAsync(ct);
        CoreTelemetry.RecordStoragePhase(phase, "runtime.learning.create.commit");
        if (previous is null) CoreTelemetry.RuntimeWrites.Add(1);
        return item;
    }
    public async Task<LearningItem> ReviewLearningItemAsync(Guid id, ReviewLearningRequest request, TimeProvider clock, CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        LearningService.ValidateReview(request);
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await PostgresPrivacyGate.LockAsync(db, ct);
        // The same D2 writer lock as UpdateInTransactionAsync, including cross-host voice/correction writers.
        var stamps = await db.Database.SqlQueryRaw<RuntimeDocumentStamp>(
            "SELECT \"Id\", \"Fingerprint\" FROM \"RuntimeDocuments\" WHERE \"Id\"=1 FOR UPDATE").ToListAsync(ct);
        _ = stamps.Single();
        CoreTelemetry.RuntimeLockWait.Record(Stopwatch.GetElapsedTime(started).TotalSeconds);
        async Task CommitAsync() {
            var phase = Stopwatch.GetTimestamp();
            await tx.CommitAsync(ct);
            CoreTelemetry.RecordStoragePhase(phase, "runtime.review.commit");
        }
        var now = clock.GetUtcNow();
        var replayRow = await db.RuntimeLearningRows.AsNoTracking().SingleOrDefaultAsync(
            x => x.Kind == nameof(RuntimeState.Reviews) && x.Id == request.RequestId, ct);
        if (replayRow is not null) {
            var replay = JsonSerializer.Deserialize<LearningReview>(replayRow.Json) ?? throw new InvalidDataException("Null persisted review.");
            if (replay.RequestId != replayRow.Id) throw new InvalidDataException("Review dictionary and record identities differ.");
            var result = LearningService.DecideReview(id, request, now, replay, null, null).Result;
            await CommitAsync(); return result;
        }
        var cardRow = await db.RuntimeLearningRows.SingleOrDefaultAsync(x => x.Kind == nameof(RuntimeState.Learning) && x.Id == id, ct);
        Guard.Require(cardRow is not null, 404, "Learning item not found.");
        var card = LearningCard(cardRow);
        var baseline = await db.RuntimeLearningRows.AsNoTracking().SingleOrDefaultAsync(
            x => x.Kind == nameof(RuntimeState.TrainingBaselines) && x.Id == id, ct);
        RuntimeLearningControlRow? control = null;
        if (baseline is not null) {
            _ = LearningCard(baseline); // Retain the canonical baseline identity check.
            control = await db.RuntimeLearningState.SingleAsync(x => x.Id == 1, ct);
        }
        var change = LearningService.DecideReview(id, request, now, null, card, control?.NextTrainingSequence);
        cardRow.Json = JsonSerializer.Serialize(change.Result);
        cardRow.DueAt = change.Result.DueAt.ToUniversalTime();
        db.RuntimeLearningRows.Add(new() { Kind = nameof(RuntimeState.Reviews), Id = request.RequestId, Json = JsonSerializer.Serialize(change.AddedReview) });
        if (change.ManualSequence is long sequence) {
            // The existing composite PK rejects an orphan/duplicate sequence and rolls back the whole review.
            db.RuntimeLearningRows.Add(new() { Kind = nameof(RuntimeState.TrainingManualSequences), Id = request.RequestId, Json = JsonSerializer.Serialize(sequence) });
            control!.NextTrainingSequence = change.NextSequence!.Value;
        }
        await db.SaveChangesAsync(ct);
        await CommitAsync();
        CoreTelemetry.RuntimeWrites.Add(1);
        return change.Result;
    }
    private static LearningItem LearningCard(RuntimeLearningRow row)
    {
        var card = JsonSerializer.Deserialize<LearningItem>(row.Json) ?? throw new InvalidDataException("Null persisted learning record.");
        if (card.Id != row.Id) throw new InvalidDataException("Learning dictionary and record identities differ.");
        return card;
    }
    public async Task<IReadOnlyList<ReviewEvent>> ReadLearningHistoryAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await PostgresPrivacyGate.LockAsync(db, ct);
        // Canonical JSON stores GUIDs in D format; lower supports older uppercase hex without
        // casting unrelated/unknown payloads. Match the additive partial expression index.
        var key = id.ToString("D");
        var rows = await db.RuntimeLearningRows.FromSqlInterpolated($"""
            SELECT "Kind","Id","Json","DueAt" FROM "RuntimeLearningRows"
            WHERE "Kind"='Reviews' AND lower("Json"->'Event'->>'LearningItemId')={key}
            """).AsNoTracking().ToArrayAsync(ct);
        var events = rows.Select(row => {
            var review = JsonSerializer.Deserialize<LearningReview>(row.Json) ?? throw new InvalidDataException("Null persisted learning review.");
            if (review.RequestId != row.Id || review.Event is null || review.Event.LearningItemId != id)
                throw new InvalidDataException("Learning review identity differs from its projection.");
            return review.Event;
        }).OrderByDescending(x => x.ReviewedAt).ToArray();
        // No card lookup: original history survives a card's removal, as in the existing API.
        await tx.CommitAsync(ct); return events;
    }
    public async Task<LearningItem?> ReadLearningItemAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await PostgresPrivacyGate.LockAsync(db, ct);
        var row = await db.RuntimeLearningRows.AsNoTracking().SingleOrDefaultAsync(x => x.Kind == nameof(RuntimeState.Learning) && x.Id == id, ct);
        var result = row is null ? null : LearningCard(row);
        await tx.CommitAsync(ct); return result;
    }
    public async Task<IReadOnlyList<LearningItem>> ReadLearningItemsAsync(bool dueOnly, DateTimeOffset cutoff, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await PostgresPrivacyGate.LockAsync(db, ct);
        var upper = LearningReadProjection.CandidateUpperBound(cutoff);
        var rows = await db.RuntimeLearningRows.AsNoTracking().Where(x => x.Kind == nameof(RuntimeState.Learning) &&
            (!dueOnly || x.DueAt == null || x.DueAt <= upper)).ToArrayAsync(ct);
        var result = rows.Select(LearningCard).Where(x => !dueOnly || x.DueAt <= cutoff).OrderBy(x => x.DueAt).ToArray();
        await tx.CommitAsync(ct); return result;
    }
    public async Task<LearningDuePage> ReadDueLearningPageAsync(DateTimeOffset cutoff, int size = 50, LearningDueCursor? after = null, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var privacy = await PostgresPrivacyGate.LockAsync(db, ct);
        LearningReadProjection.Validate(cutoff, size, after, privacy.Epoch);
        if (await db.RuntimeLearningRows.AnyAsync(x => x.Kind == nameof(RuntimeState.Learning) && x.DueAt == null, ct))
            throw new InvalidDataException("A learning index timestamp is missing; repair the projection before paging.");
        var upper = LearningReadProjection.CandidateUpperBound(cutoff);
        FormattableString query;
        if (after is null) query = $"""
            SELECT "Kind","Id","Json","DueAt" FROM "RuntimeLearningRows"
            WHERE "Kind"='Learning' AND "DueAt"<={upper}
            ORDER BY "DueAt","Id" LIMIT {size + 1}
            """;
        else query = $"""
            SELECT "Kind","Id","Json","DueAt" FROM "RuntimeLearningRows"
            WHERE "Kind"='Learning' AND "DueAt"<={upper} AND ("DueAt","Id")>({after.IndexedDueAt},{after.Id})
            ORDER BY "DueAt","Id" LIMIT {size + 1}
            """;
        var rows = await db.RuntimeLearningRows.FromSqlInterpolated(query).AsNoTracking().ToArrayAsync(ct);
        var scanned = rows.Take(size).ToArray();
        var items = scanned.Select(LearningCard).Where(x => x.DueAt <= cutoff).ToArray();
        var next = rows.Length > size ? new LearningDueCursor(privacy.Epoch, cutoff, scanned[^1].DueAt!.Value, scanned[^1].Id) : null;
        await tx.CommitAsync(ct); return new(items, next, rows.Length);
    }
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
