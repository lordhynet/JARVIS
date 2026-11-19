using System.Security.Cryptography;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;
using S = Jarvis.Contracts.RecordingTranscriptStates;

namespace Jarvis.Orchestrator.Recordings;

public sealed class RecordingTranscriptService(IRecordingStore store, IRecordingAudioFiles files,
    IRecordingTranscriber provider, UsageBudget budget, RecordingService recordings, TimeProvider clock)
{
    private sealed record Claim(Guid RecordingId, Guid QuoteId, Guid OperationId, RecordingTranscriptPiece Piece, RecordingTranscriptionPrice Price);
    public Task<RecordingTranscriptProgress?> GetAsync(Guid id, bool? locked, CancellationToken ct = default)
    {
        RecordingService.RequireManualUnlocked(locked);
        return store.ReadWithRuntimeAsync((state, runtime) => View(Find(state, id), runtime), ct);
    }
    public Task<RecordingTranscriptProgress> QuoteAsync(Guid id, QuoteRecordingTranscriptRequest request, CancellationToken ct = default)
    {
        RecordingService.RequireManualUnlocked(request.Locked); var price = provider.Pricing();
        Guard.Require(files.Ready && price.PerMinute > 0 && !string.IsNullOrWhiteSpace(price.Model), 503, "Recording transcription is not configured.");
        return store.UpdateAsync((state, runtime) => {
            var session = Find(state, id); Eligible(session); var recording = session.View;
            Guard.Require(recording.Revision == request.Revision, 409, "Recording changed. Reload before quoting.");
            var previous = session.TranscriptJob;
            Guard.Require(previous?.Pieces.Any(p => p.Status is S.Preparing or S.Running or S.Uncertain) != true, 409, "Resolve the previous transcription attempt before another quote.");
            _ = Transcript(runtime, recording);
            var pieces = state.Chunks.Values.Where(c => c.RecordingId == id).OrderBy(c => c.Sequence).ThenBy(c => c.Track, StringComparer.Ordinal).Select(chunk => {
                var old = previous?.Pieces.FirstOrDefault(p => p.Id == chunk.Id);
                if (old is not null && old.Status != S.Pending) { Guard.Require(old.Sha256 == chunk.Sha256, 409, "Previously transcribed audio changed."); return old; }
                // Existing capture chunks are already below 10 MB and the provider's three-minute
                // limit. Keeping their boundaries avoids new codecs and preserves exact offsets.
                Guard.Require(chunk.Bytes is > 44 and <= 10_000_000 && chunk.Samples is > 0 and <= RecordingProtocol.SampleRate * 180, 400, "Audio piece exceeds the existing transcription limit.");
                var maximum = decimal.Ceiling((decimal)chunk.Samples / RecordingProtocol.SampleRate) / 60m * price.PerMinute * 2m;
                return new RecordingTranscriptPiece(chunk.Id, chunk.Track, chunk.Sequence, chunk.Sha256, chunk.Samples, maximum, budget.EstimateGbp(maximum));
            }).ToArray();
            Guard.Require(pieces.Length is > 0 and <= 360, 409, "No uploaded audio is available for transcription.");
            var pending = pieces.Where(p => p.Status == S.Pending).ToArray();
            var job = new RecordingTranscriptJob(Guid.NewGuid(), price.Model, price.PerMinute, pending.Sum(p => p.MaximumUsd),
                decimal.Ceiling(pending.Sum(p => p.MaximumGbp) * 10000m) / 10000m, clock.GetUtcNow().AddMinutes(10), pieces, Revision: 0);
            return Task.FromResult(View(Put(state, session, job), runtime)!);
        }, ct);
    }
    public async Task<RecordingTranscriptProgress> AdvanceAsync(Guid id, AdvanceRecordingTranscriptRequest request, CancellationToken ct = default)
    {
        RecordingService.RequireManualUnlocked(request.Locked);
        Guard.Require(request.RequestId != Guid.Empty && request.ConfirmCloudProcessing, 400, "Explicit cloud transcription consent and an operation ID are required.");
        var claim = await store.UpdateAsync<Claim?>((state, runtime) => {
            var session = Find(state, id); var job = session.TranscriptJob;
            Guard.Require(job is not null && job.Id == request.QuoteId, 409, "Review the current transcription quote.");
            if (job.Operations?.ContainsKey(request.RequestId) == true) return Task.FromResult<Claim?>(null);
            if (job.Status is S.Complete or S.Partial && job.Pieces.All(p => p.Status is S.Succeeded or S.Failed)) return Task.FromResult<Claim?>(null);
            Eligible(session); var price = provider.Pricing();
            Guard.Require(job.Revision == request.JobRevision, 409, "Transcription progress changed. Reload it first.");
            var active = job.Pieces.FirstOrDefault(p => p.Status is S.Preparing or S.Running);
            if (active is not null) {
                Guard.Require(active.StartedAt <= clock.GetUtcNow().AddMinutes(-10), 409, "A transcription request is still in flight. Do not replay it.");
                if (active.Status == S.Preparing && !runtime.UsageReservations.ContainsKey(active.ReservationId!.Value))
                    job = Replace(job, active with { Status = S.Pending, ReservationId = null, OperationId = null, StartedAt = null }) with { Status = S.Paused, ErrorCode = "stopped_before_reservation" };
                else job = Uncertain(runtime, job, active);
                Put(state, session, job); return Task.FromResult<Claim?>(null);
            }
            if (job.Pieces.Any(p => p.Status == S.Uncertain)) {
                Guard.Require(request.ContinueAfterReconciliation, 409, "Reconcile the provider outcome and explicitly confirm continuation. Failed pieces are never replayed.");
                foreach (var piece in job.Pieces.Where(p => p.Status == S.Uncertain).ToArray()) {
                    Guard.Require(piece.ReservationId is { } reservation && runtime.UsageReservations.TryGetValue(reservation, out var charge) &&
                        !charge.ReconciliationRequired && charge.ActualUsd is not null && runtime.UsageReconciliations.Values.Any(r => r.ReservationId == reservation),
                        409, "Verified billing reconciliation is required before continuing.");
                    job = Replace(job, piece with { Status = S.Failed });
                }
                if (job.Model != price.Model || job.PerMinute != price.PerMinute) {
                    Put(state, session, job with { Status = S.Paused, ErrorCode = "pricing_changed_request_new_quote" }); return Task.FromResult<Claim?>(null);
                }
            }
            Guard.Require(job.Model == price.Model && job.PerMinute == price.PerMinute, 409, "Reviewed transcription pricing changed. Request a fresh quote.");
            _ = Transcript(runtime, session.View);
            if (job.ConfirmedAt is null) {
                Guard.Require(request.ConfirmQuote && job.ExpiresAt > clock.GetUtcNow(), 409, "Confirm this exact, unexpired quote before transcription.");
                job = job with { ConfirmedAt = clock.GetUtcNow() };
            }
            var next = job.Pieces.FirstOrDefault(p => p.Status == S.Pending);
            if (next is null) { Complete(state, runtime, session, job); return Task.FromResult<Claim?>(null); }
            Guard.Require(budget.EstimateGbp(next.MaximumUsd) <= next.MaximumGbp, 409, "The GBP maximum increased. Request a new quote before dispatch.");
            var chunk = state.Chunks.GetValueOrDefault(next.Id);
            Guard.Require(chunk is not null && chunk.RecordingId == id && chunk.Sha256 == next.Sha256, 409, "Quoted audio is no longer available.");
            var operation = new Dictionary<Guid, Guid>(job.Operations ?? new Dictionary<Guid, Guid>()) { [request.RequestId] = next.Id };
            Guard.Require(operation.Count <= 720, 409, "Transcription operation limit reached.");
            next = next with { Status = S.Preparing, ReservationId = AgentRuntime.StableId($"recording-stt-v1:{job.Id:N}:{next.Id:N}:{request.RequestId:N}"), StartedAt = clock.GetUtcNow(), OperationId = request.RequestId };
            Put(state, session, Replace(job, next) with { Status = S.Preparing, ErrorCode = null, Operations = operation });
            return Task.FromResult<Claim?>(new(id, job.Id, request.RequestId, next, price));
        }, ct);
        if (claim is null) return (await GetAsync(id, false, ct))!;
        byte[]? audio = null; var reserved = false; var dispatched = false;
        try {
            await budget.ReserveAsync(claim.Piece.ReservationId!.Value, id, claim.Price.Model, claim.Piece.MaximumUsd,
                claim.Price.Limits, ct, UsageLane.Interactive, ProviderIds.OpenAi); reserved = true;
            audio = await store.UpdateAsync(async (state, runtime) => {
                var session = Current(state, claim); Eligible(session); _ = Transcript(runtime, session.View);
                Guard.Require(runtime.GbpCommitments.TryGetValue(claim.Piece.ReservationId!.Value, out var commitment) && commitment.ReservedGbp <= claim.Piece.MaximumGbp,
                    409, "The reserved GBP maximum exceeds the confirmed quote.");
                var chunk = state.Chunks.GetValueOrDefault(claim.Piece.Id);
                Guard.Require(chunk is not null && chunk.Sha256 == claim.Piece.Sha256, 409, "Quoted audio is unavailable.");
                var bytes = await files.ReadAsync(chunk, ct);
                try { Guard.Require(RecordingAudio.Samples(bytes) == claim.Piece.Samples, 409, "Quoted audio changed."); }
                catch { CryptographicOperations.ZeroMemory(bytes); throw; }
                Put(state, session, Replace(session.TranscriptJob!, claim.Piece with { Status = S.Running }) with { Status = S.Running }); return bytes;
            }, ct);
            dispatched = true;
            var text = (await provider.TranscribeAsync(audio, claim.Price, ct)).Trim();
            Guard.Require(text.Length <= 10000, 502, "Transcription text exceeded its piece bound.");
            await store.UpdateAsync((state, runtime) => {
                var session = Current(state, claim); var job = session.TranscriptJob!;
                var document = Transcript(runtime, session.View); var segments = (document?.Segments ?? []).ToList();
                if (text.Length > 0) segments.Add(new(claim.Piece.Track, text, claim.Piece.Sequence * RecordingProtocol.ChunkSeconds,
                    claim.Piece.Sequence * RecordingProtocol.ChunkSeconds + (double)claim.Piece.Samples / RecordingProtocol.SampleRate, claim.Piece.Id, claim.Piece.Track));
                if (segments.Count > 0) {
                    document = ImportService.SaveRecordingTranscript(runtime, session.View, segments, true, clock.GetUtcNow());
                    session = session with { View = session.View with { TranscriptId = document.Id } };
                }
                job = Replace(job, claim.Piece with { Status = S.Succeeded }) with { Status = S.Ready, ErrorCode = null };
                if (job.Pieces.All(p => p.Status != S.Pending)) Complete(state, runtime, session, job);
                else Put(state, session, job);
                return Task.FromResult(true);
            }, ct);
        } catch {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (reserved && !dispatched) try { await budget.SettleAsync(claim.Piece.ReservationId!.Value, 0, 0, 0, cleanup.Token); } catch { }
            try {
                await store.UpdateAsync((state, runtime) => {
                    var session = Current(state, claim); var job = session.TranscriptJob!;
                    job = dispatched ? Uncertain(runtime, job, claim.Piece) : Replace(job, claim.Piece with { Status = S.Pending, ReservationId = null, StartedAt = null, OperationId = null }) with { Status = S.Paused, ErrorCode = "dispatch_not_started" };
                    Put(state, session, job); return Task.FromResult(true);
                }, cleanup.Token);
            } catch { /* Privacy/deletion wins. Never recreate a removed job or transcript. */ }
            throw;
        } finally { if (audio is not null) CryptographicOperations.ZeroMemory(audio); }
        var progress = (await GetAsync(id, false, ct))!;
        if (progress.AudioDeletePending) {
            try { await recordings.RetainAudioAsync(ct); } catch (Exception error) when (error is not OperationCanceledException) { }
            progress = (await GetAsync(id, false, ct))!;
        }
        return progress;
    }
    public Task<ImportedDocument> RelabelAsync(Guid id, RelabelRecordingSegmentRequest request, CancellationToken ct = default)
    {
        RecordingService.RequireManualUnlocked(request.Locked);
        Guard.Require(request.Speaker is "user" or "interviewer" or "unknown", 400, "Choose me, interviewer or unknown.");
        return store.UpdateAsync((state, runtime) => {
            var session = Find(state, id); var document = Transcript(runtime, session.View);
            Guard.Require(document is not null && document.Revision == request.TranscriptRevision, 409, "Transcript changed. Reload it before relabelling.");
            Guard.Require(document.Segments.Any(s => s.RecordingPieceId == request.PieceId), 404, "Transcript piece not found.");
            var saved = ImportService.SaveRecordingTranscript(runtime, session.View, document.Segments.Select(s => s.RecordingPieceId == request.PieceId ? s with { Speaker = request.Speaker } : s).ToArray(), document.Partial, clock.GetUtcNow());
            Put(state, session, session.TranscriptJob!); return Task.FromResult(saved);
        }, ct);
    }
    private static RecordingSession Find(RecordingState state, Guid id) { Guard.Require(state.Sessions.TryGetValue(id, out var value), 404, "Recording not found."); return value; }
    private static void Eligible(RecordingSession session) => Guard.Require(!session.View.AudioDeleted && session.View.StoppedAt is not null && session.View.Status is "Complete" or "Incomplete" &&
        session.View.ConfirmStorageConsent && session.View.ConfirmRecordingAuthorised, 409, "Finish capture/upload and preserve its consented audio before transcription.");
    private static ImportedDocument? Transcript(RuntimeState runtime, InterviewRecording recording)
    {
        var id = recording.TranscriptId ?? AgentRuntime.StableId("recording-transcript-v1:" + recording.Id.ToString("N"));
        if (!runtime.ImportedDocuments.TryGetValue(id, out var document)) return null;
        Guard.Require(document.DeletedAt is null && document.SourceRecordingId == recording.Id, 409, "Transcript was deleted or belongs to other content."); return document;
    }
    private static RecordingSession Current(RecordingState state, Claim claim)
    {
        var session = Find(state, claim.RecordingId); var piece = session.TranscriptJob?.Pieces.FirstOrDefault(p => p.Id == claim.Piece.Id);
        Guard.Require(session.TranscriptJob?.Id == claim.QuoteId && piece?.OperationId == claim.OperationId && piece.Status is S.Preparing or S.Running,
            409, "Transcription ownership changed. The old result cannot be saved."); return session;
    }
    private static RecordingTranscriptJob Replace(RecordingTranscriptJob job, RecordingTranscriptPiece piece) => job with { Pieces = job.Pieces.Select(p => p.Id == piece.Id ? piece : p).ToArray() };
    private static RecordingTranscriptJob Uncertain(RuntimeState runtime, RecordingTranscriptJob job, RecordingTranscriptPiece piece)
    {
        if (piece.ReservationId is { } id && runtime.UsageReservations.TryGetValue(id, out var charge)) runtime.UsageReservations[id] = charge with { ReconciliationRequired = true };
        return Replace(job, piece with { Status = S.Uncertain }) with { Status = S.Uncertain, ErrorCode = "provider_outcome_requires_reconciliation" };
    }
    private static RecordingSession Put(RecordingState state, RecordingSession session, RecordingTranscriptJob job)
    {
        var updated = session with { View = session.View with { Revision = session.View.Revision + 1 }, TranscriptJob = job with { Revision = job.Revision + 1 } };
        state.Sessions[updated.View.Id] = updated; return updated;
    }
    private void Complete(RecordingState state, RuntimeState runtime, RecordingSession session, RecordingTranscriptJob job)
    {
        var complete = session.View.Status == "Complete" && job.Pieces.All(p => p.Status == S.Succeeded) &&
            session.FinalCounts is not null && session.View.Tracks.All(track => job.Pieces.Where(p => p.Track == track).OrderBy(p => p.Sequence)
                .Select(p => p.Sequence).SequenceEqual(Enumerable.Range(0, session.FinalCounts[track])));
        var document = Transcript(runtime, session.View);
        if (document is not null) _ = ImportService.SaveRecordingTranscript(runtime, session.View, document.Segments, !complete, clock.GetUtcNow());
        var view = session.View;
        if (complete && view.DeleteAudioAfterTranscription) view = view with { AudioRetainUntil = clock.GetUtcNow() };
        Put(state, session with { View = view }, job with { Status = complete ? S.Complete : S.Partial });
    }
    internal static RecordingSession CaptureBecameIncomplete(RecordingSession session, RuntimeState runtime)
    {
        if (session.TranscriptJob is not { Status: S.Complete } job) return session;
        if (session.View.TranscriptId is { } id && runtime.ImportedDocuments.TryGetValue(id, out var document) && document.DeletedAt is null)
            runtime.ImportedDocuments[id] = document with { Partial = true, Revision = document.Revision + 1 };
        return session with { TranscriptJob = job with { Status = S.Partial, Revision = job.Revision + 1 } };
    }
    private static RecordingTranscriptProgress? View(RecordingSession session, RuntimeState runtime)
    {
        var job = session.TranscriptJob; if (job is null) return null;
        return new(session.View.Id, job.Id, job.Revision, session.View.Revision, job.Status, job.Model, job.MaximumUsd, job.MaximumGbp, job.ExpiresAt,
            job.Pieces.Select(p => new RecordingTranscriptPieceView(p.Id, p.Track, p.Sequence, p.Sequence * RecordingProtocol.ChunkSeconds,
                p.Sequence * RecordingProtocol.ChunkSeconds + (double)p.Samples / RecordingProtocol.SampleRate, p.Status, p.MaximumUsd, p.MaximumGbp, p.ReservationId)).ToArray(),
            Transcript(runtime, session.View), job.ErrorCode, session.View.DeleteAudioAfterTranscription && job.Status == S.Complete && !session.View.AudioDeleted);
    }
}
