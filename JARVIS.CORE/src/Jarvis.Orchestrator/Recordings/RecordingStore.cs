using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Recordings;

public sealed record RecordingSession(InterviewRecording View, string CredentialHash, string RequestHash,
    bool Discontinuous = false, IReadOnlyDictionary<string, int>? FinalCounts = null, string OwnerId = "owner",
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] RecordingTranscriptJob? TranscriptJob = null);
public sealed record RecordingChunk(Guid Id, Guid RecordingId, string Track, int Sequence, string Sha256, int Samples,
    Guid BlobId, long Epoch, long Bytes, DateTimeOffset At);
public sealed class RecordingState
{
    [System.Text.Json.Serialization.JsonIgnore] public Func<Guid, Func<ConversationSession, ConversationSession>, Task<ConversationSession?>>? UpdateSession { get; set; }
    public long Epoch { get; set; }
    public Dictionary<Guid, RecordingSession> Sessions { get; set; } = [];
    public Dictionary<Guid, RecordingChunk> Chunks { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore] public long StoredBytes => Chunks.Values.Sum(c => c.Bytes + 32);
}
public enum RecordingCommitOutcome { Rejected, Committed, Unknown }
public interface IRecordingStore
{
    Task<T> ReadAsync<T>(Func<RecordingState, T> read, CancellationToken ct = default);
    Task<T> ReadWithRuntimeAsync<T>(Func<RecordingState, RuntimeState, T> read, CancellationToken ct = default);
    Task<T> UpdateAsync<T>(Func<RecordingState, RuntimeState, Task<T>> update, CancellationToken ct = default);
    Task<T> UpdatePreparedAsync<TPlan, T>(Func<RecordingState, RuntimeState, TPlan> plan,
        Func<TPlan, CancellationToken, Task> prepare, Func<RecordingState, RuntimeState, TPlan, Task<T>> update,
        Func<TPlan, RecordingCommitOutcome, Task>? finish = null, CancellationToken ct = default);
}
public interface IRecordingAudioFiles
{
    bool Ready { get; }
    long BytesOnDisk { get; }
    Task WriteAsync(RecordingChunk chunk, ReadOnlyMemory<byte> audio, CancellationToken ct);
    Task<byte[]> ReadAsync(RecordingChunk chunk, CancellationToken ct);
    Task DeleteAsync(RecordingChunk chunk, CancellationToken ct);
    Task DeleteRecordingAsync(Guid recordingId, CancellationToken ct);
    Task EraseAsync(CancellationToken ct);
}
public sealed class MemoryRecordingStore(InMemoryRuntimeStore runtime, MemoryPrivacyBoundary boundary, ICoreTransaction? transaction = null) : IRecordingStore
{
    private static RecordingState Copy(RecordingState s) => JsonSerializer.Deserialize<RecordingState>(JsonSerializer.Serialize(s))!;
    public Task<T> ReadWithRuntimeAsync<T>(Func<RecordingState, RuntimeState, T> read, CancellationToken ct = default) => boundary.Run(() => {
        var copy = Copy(boundary.Recordings); copy.Epoch = boundary.Status.Epoch; return runtime.ReadAsync(s => read(copy, s), ct);
    });
    public Task<T> ReadAsync<T>(Func<RecordingState, T> read, CancellationToken ct = default) => boundary.Run(() => {
        ct.ThrowIfCancellationRequested(); var copy = Copy(boundary.Recordings); copy.Epoch = boundary.Status.Epoch; return Task.FromResult(read(copy));
    });
    private Task<T> CommitAsync<T>(Func<RecordingState, RuntimeState, Task<T>> update, CancellationToken ct) => boundary.Run(async () => {
        var copy = Copy(boundary.Recordings); copy.Epoch = boundary.Status.Epoch; RecordingState? saved = null;
        T result;
        if (transaction is not null) result = await transaction.ExecuteAsync(async writes => {
            copy.UpdateSession = writes.UpdateSessionAsync; var value = await update(copy, writes.Runtime); saved = Copy(copy); return value;
        }, ct);
        else result = await runtime.UpdateAsync(s => { var value = update(copy, s).GetAwaiter().GetResult(); saved = Copy(copy); return value; }, ct);
        boundary.Recordings = saved!; return result;
    });
    public async Task<T> UpdateAsync<T>(Func<RecordingState, RuntimeState, Task<T>> update, CancellationToken ct = default)
    {
        await boundary.RecordingIo.WaitAsync(ct);
        try { return await CommitAsync(update, ct); } finally { boundary.RecordingIo.Release(); }
    }
    public async Task<T> UpdatePreparedAsync<TPlan, T>(Func<RecordingState, RuntimeState, TPlan> plan,
        Func<TPlan, CancellationToken, Task> prepare, Func<RecordingState, RuntimeState, TPlan, Task<T>> update,
        Func<TPlan, RecordingCommitOutcome, Task>? finish = null, CancellationToken ct = default)
    {
        await boundary.RecordingIo.WaitAsync(ct); var planned = false; TPlan value = default!; var outcome = RecordingCommitOutcome.Rejected;
        try {
            value = await ReadWithRuntimeAsync(plan, ct); planned = true;
            // Keep the recording/erasure gate, but release the runtime mutex during file IO.
            await prepare(value, ct);
            var result = await CommitAsync((state, current) => update(state, current, value), ct);
            outcome = RecordingCommitOutcome.Committed; return result;
        } finally {
            try { if (planned && finish is not null) try { await finish(value, outcome); } catch when (outcome != RecordingCommitOutcome.Committed) { } }
            finally { boundary.RecordingIo.Release(); }
        }
    }
}
