using System.Security.Cryptography;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;

namespace Jarvis.Orchestrator.Recordings;

public sealed class RecordingService(IRecordingStore store, IRecordingAudioFiles files, TimeProvider clock)
{
    public const long MaximumBytes = 2L * 1024 * 1024 * 1024;
    public bool Ready => files.Ready;
    public Task<IReadOnlyList<InterviewRecording>> ListAsync(CancellationToken ct = default) => store.ReadAsync<IReadOnlyList<InterviewRecording>>(
        s => s.Sessions.Values.Select(x => x.View).OrderByDescending(x => x.PreparedAt).ToArray(), ct);
    public Task<bool> RetainAudioAsync(CancellationToken ct = default) => store.UpdateAsync(async (s, runtime) => {
        RecordingRetention.CloseExpiredCaptures(s, runtime, clock.GetUtcNow());
        await RecordingRetention.ApplyAsync(s, runtime, files, clock.GetUtcNow(), ct); return true;
    }, ct);
    public Task<InterviewRecording> PrepareAsync(PrepareRecordingRequest request, CancellationToken ct = default)
    {
        RequireManualUnlocked(request.Locked);
        Guard.Require(files.Ready, 503, "Interview recording is disabled until its encryption key and storage are configured.");
        Guard.Require(request.RequestId != Guid.Empty && request.ConfirmStorageConsent && request.ConfirmRecordingAuthorised,
            400, "Explicit recording authorization and storage consent are required for each interview.");
        var title = Guard.Text(request.Title, "Recording title", 120);
        Guard.Text(request.DeviceId, "Device ID", 80);
        var hash = PrivacyData.Hash(JsonSerializer.Serialize(request));
        return store.UpdatePreparedAsync((s, _) => !s.Sessions.ContainsKey(request.RequestId),
            (isNew, _) => { if (isNew) Guard.Require(files.BytesOnDisk < MaximumBytes, 409, "Recording storage is full. Remove existing audio before preparing another interview."); return Task.CompletedTask; },
            (s, runtime, _) => {
            if (s.Sessions.TryGetValue(request.RequestId, out var replay)) { Guard.Require(replay.RequestHash == hash, 409, "Recording operation ID was reused."); return Task.FromResult(replay.View); }
            var now = clock.GetUtcNow();
            RecordingRetention.CloseExpiredCaptures(s, runtime, now);
            Guard.Require(s.Sessions.Count < 1000 && s.StoredBytes < MaximumBytes && s.Sessions.Values.Count(r => r.View.Status == "Prepared" && r.View.OfferExpiresAt > now) < 10,
                409, "Recording storage or preparation capacity reached. Delete or finish existing recordings.");
            Guard.Require(runtime.Devices.TryGetValue(request.DeviceId, out var device) && device.Registration.Capabilities.CanListen &&
                device.Registration.Kind is DeviceKind.AndroidCompanion or DeviceKind.WindowsAgent &&
                !device.Registration.Capabilities.Extra.Contains(VoiceAccessRules.BrowserCompanionTag), 400, "Choose an enrolled native phone or laptop microphone.");
            Guard.Require(runtime.DeviceCredentials.TryGetValue(request.DeviceId, out var credential) && !credential.Revoked && credential.Purpose is null,
                409, "Enroll this native device before preparing a recording.");
            Guard.Require(!s.Sessions.Values.Any(r => r.View.DeviceId == request.DeviceId &&
                (r.View.Status == "Prepared" && r.View.OfferExpiresAt > now || r.View.Status == "Recording" && r.View.StartedAt > now.AddHours(-3))), 409, "This device already has a prepared or active recording.");
            var tracks = device.Registration.Kind == DeviceKind.WindowsAgent ? new[] { "user", "interviewer" } : ["unknown"];
            var recording = new InterviewRecording(request.RequestId, request.DeviceId, title, "Prepared", tracks, now, now.AddMinutes(10), DeleteAudioAfterTranscription: request.DeleteAudioAfterTranscription);
            s.Sessions.Add(recording.Id, new(recording, credential.SecretHash, hash, OwnerId: DeviceCredentialContext.CurrentActor?.SubjectId ?? "owner")); return Task.FromResult(recording);
        }, ct: ct);
    }
    public static void RequireManualUnlocked(bool? locked)
    {
        Guard.Require(CoreInvocationContext.Origin == InvocationOrigin.ManualClient && locked == false, 403, "Use an unlocked owner client for recording management.");
    }
    private static void RequireCapture(RuntimeState runtime, RecordingSession session, string deviceId)
    {
        var actor = DeviceCredentialContext.RequirePairedActor(); DeviceCredentialContext.RequireCurrent(runtime, deviceId);
        Guard.Require(!actor.IsOwnerBrowser && actor.DeviceId == deviceId && session.View.DeviceId == deviceId && actor.CredentialHash == session.CredentialHash,
            403, "This capture job belongs to a different enrolled device or credential.");
    }
    public async Task<RecordingCaptureResult> CaptureAsync(RecordingCaptureRequest request, CancellationToken ct = default)
    {
        Guard.Require(request.Action is "poll" or "start" or "chunk" or "finish", 400, "Unknown recording capture operation.");
        var actor = DeviceCredentialContext.RequirePairedActor();
        Guard.Require(actor.DeviceId == request.DeviceId && !actor.IsOwnerBrowser, 403, "Use this native device's own capture credential.");
        if (request.Action == "poll") return await store.ReadWithRuntimeAsync((s, runtime) => {
            DeviceCredentialContext.RequireCurrent(runtime, request.DeviceId); var now = clock.GetUtcNow();
            var offers = s.Sessions.Values.Where(r => r.View.DeviceId == request.DeviceId && r.CredentialHash == actor.CredentialHash &&
                (r.View.Status == "Prepared" && r.View.OfferExpiresAt > now || r.View.Status is "Recording" or "Incomplete" && r.View.StartedAt > now.AddDays(-1)))
                .OrderByDescending(r => r.View.PreparedAt).Take(10).Select(r => new RecordingCaptureOffer(r.View.Id, r.View.Status, r.View.Tracks, r.View.OfferExpiresAt)).ToArray();
            return new RecordingCaptureResult(files.Ready, offers);
        }, ct);
        Guard.Require(request.RecordingId is { } id && id != Guid.Empty, 400, "A prepared recording ID is required.");
        byte[]? audio = null; var samples = 0; string? sha = null;
        try {
        if (request.Action == "chunk") {
            Guard.Require(request.AudioBase64 is { Length: > 0 and <= 2_560_064 } && request.Sequence is >= 0 and < 180 && request.Sha256 is { Length: 64 }, 400, "Chunk payload, sequence or SHA-256 is invalid.");
            try { audio = Convert.FromBase64String(request.AudioBase64); } catch (FormatException) { throw new DomainException(400, "Chunk audio is not valid base64."); }
            samples = RecordingAudio.Samples(audio); sha = RecordingAudio.Hash(audio);
            Guard.Require(sha.Equals(request.Sha256, StringComparison.OrdinalIgnoreCase), 400, "Chunk SHA-256 does not match its audio.");
            return await UploadChunkAsync(request, audio, sha, samples, ct);
        }
            return await store.UpdateAsync((s, runtime) => {
                Guard.Require(s.Sessions.TryGetValue(request.RecordingId!.Value, out var session), 404, "Recording capture job not found.");
                RequireCapture(runtime, session, request.DeviceId); var recording = session.View; var now = clock.GetUtcNow();
                Guard.Require(!recording.AudioDeleted, 409, "Audio was already deleted.");
                if (request.Action == "start") {
                    Guard.Require(request.Locked == false && request.ConfirmStorageConsent && request.ConfirmRecordingAuthorised, 403, "Unlock this device and explicitly confirm recording authorization and storage consent.");
                    if (recording.Status == "Recording" && runtime.Devices.GetValueOrDefault(request.DeviceId)?.Recording is { } block && block.RecordingId == recording.Id && block.Until > now) return Task.FromResult(Result(s, recording));
                    Guard.Require(files.Ready && recording.Status == "Prepared" && recording.OfferExpiresAt > now, 409, "Recording offer expired or was already started.");
                    Guard.Require(!(runtime.AudioLease is { } lease && lease.DeviceId == request.DeviceId && lease.ExpiresAt > now) &&
                        !(runtime.AudioDrainLease?.DeviceId == request.DeviceId && runtime.AudioDrainUntil > now), 409, "Stop JARVIS voice and wait for its audio release before recording.");
                    var device = runtime.Devices[request.DeviceId]; Guard.Require(!(device.Recording?.Until > now), 409, "Another recording already owns this microphone.");
                    recording = recording with { Status = "Recording", StartedAt = now, ConsentAt = now, ConfirmStorageConsent = true, ConfirmRecordingAuthorised = true,
                        ConsentVersion = "interview-private-practice-v1", AudioRetainUntil = now.AddDays(30), Revision = recording.Revision + 1 };
                    runtime.Devices[request.DeviceId] = device with { Recording = new(recording.Id, now.AddSeconds(RecordingProtocol.MaximumSeconds)) };
                }
                else {
                    Guard.Require(recording.StartedAt is not null && recording.Status is "Recording" or "Incomplete" or "Complete" && recording.StartedAt > now.AddDays(-1), 409, "This recording is not accepting capture data.");
                        var counts = request.FinalCounts;
                        Guard.Require(counts is not null && counts.Count == recording.Tracks.Count && recording.Tracks.All(t => counts.TryGetValue(t, out var count) && count is >= 0 and <= 180), 400, "Declare a bounded final chunk count for every track.");
                        Guard.Require(session.FinalCounts is null || recording.Tracks.All(t => session.FinalCounts[t] == counts[t]), 409, "The declared recording end cannot be changed.");
                        var complete = !request.Discontinuous && !session.Discontinuous;
                        var ends = new List<int>();
                        foreach (var track in recording.Tracks) {
                            var chunks = s.Chunks.Values.Where(c => c.RecordingId == recording.Id && c.Track == track).OrderBy(c => c.Sequence).ToArray();
                            var expected = counts[track];
                            complete &= expected > 0 && chunks.Length == expected && chunks.Select(c => c.Sequence).SequenceEqual(Enumerable.Range(0, expected)) && chunks.Take(Math.Max(0, chunks.Length - 1)).All(c => c.Samples == RecordingProtocol.ChunkSeconds * RecordingProtocol.SampleRate);
                            if (chunks.Length > 0) ends.Add(chunks[^1].Sequence * RecordingProtocol.ChunkSeconds * RecordingProtocol.SampleRate + chunks[^1].Samples);
                        }
                        complete &= ends.Count == recording.Tracks.Count && ends.Max() - ends.Min() <= RecordingProtocol.SampleRate;
                        if (session.FinalCounts is not null && recording.StoppedAt is not null && recording.Status == (complete ? "Complete" : "Incomplete") && (!request.Discontinuous || session.Discontinuous)) return Task.FromResult(Result(s, recording));
                        recording = recording with { Status = complete ? "Complete" : "Incomplete", StoppedAt = recording.StoppedAt ?? now, Revision = recording.Revision + 1 };
                        session = session with { FinalCounts = counts, Discontinuous = session.Discontinuous || request.Discontinuous }; ClearBlock(runtime, recording);
                }
                s.Sessions[recording.Id] = session with { View = recording }; return Task.FromResult(Result(s, recording));
            }, ct);
        }
        // An uncertain DB commit must never delete a possibly acknowledged blob. Random blob IDs
        // keep retries independent; delete/retention/erasure remove crash orphans for the recording.
        finally { if (audio is not null) CryptographicOperations.ZeroMemory(audio); }
    }
    private sealed record ChunkPlan(RecordingChunk Chunk, bool Existing);
    private ChunkPlan PlanChunk(RecordingState state, RuntimeState runtime, RecordingCaptureRequest request, string sha, int samples, int bytes)
    {
        Guard.Require(state.Sessions.TryGetValue(request.RecordingId!.Value, out var session), 404, "Recording capture job not found.");
        RequireCapture(runtime, session, request.DeviceId); var recording = session.View;
        Guard.Require(files.Ready && !recording.AudioDeleted && recording.StartedAt is not null && recording.Status is "Recording" or "Incomplete" or "Complete" &&
            recording.StartedAt > clock.GetUtcNow().AddDays(-1), 409, "This recording is not accepting capture data.");
        Guard.Require(recording.Tracks.Contains(request.Track!), 400, "Choose a declared recording track.");
        var id = AgentRuntime.StableId($"recording-chunk-v1:{recording.Id:N}:{request.Track}:{request.Sequence}");
        if (state.Chunks.TryGetValue(id, out var previous)) {
            Guard.Require(previous.Sha256 == sha && previous.Samples == samples, 409, "Chunk sequence was reused for different audio."); return new(previous, true);
        }
        Guard.Require(recording.Status != "Complete" && state.StoredBytes + bytes + 32 <= MaximumBytes && state.Chunks.Count < 10000, 409, "Recording is complete or its storage capacity is full.");
        Guard.Require(session.FinalCounts is null || session.FinalCounts[request.Track!] > request.Sequence, 409, "This sequence is outside the declared recording end.");
        return new(new(id, recording.Id, request.Track!, request.Sequence!.Value, sha, samples, Guid.NewGuid(), state.Epoch, bytes, clock.GetUtcNow()), false);
    }
    private Task<RecordingCaptureResult> UploadChunkAsync(RecordingCaptureRequest request, byte[] audio, string sha, int samples, CancellationToken ct) =>
        store.UpdatePreparedAsync((s, r) => PlanChunk(s, r, request, sha, samples, audio.Length), async (plan, token) => {
            if (plan.Existing) {
                var persisted = await files.ReadAsync(plan.Chunk, token);
                try { Guard.Require(RecordingAudio.Hash(persisted) == sha, 409, "Stored chunk integrity must be recovered before acknowledging it."); }
                finally { CryptographicOperations.ZeroMemory(persisted); }
            } else {
                // Physical orphan admission is serialized outside D2. The metadata quota
                // below uses only stored chunk bytes, never a directory scan under D2.
                Guard.Require(files.BytesOnDisk + audio.Length + 32 <= MaximumBytes, 409, "Recording storage is full.");
                await files.WriteAsync(plan.Chunk, audio, token);
            }
        }, (s, runtime, plan) => {
            var current = PlanChunk(s, runtime, request, sha, samples, audio.Length);
            Guard.Require(current.Existing == plan.Existing && current.Chunk.Epoch == plan.Chunk.Epoch && (!plan.Existing || current.Chunk.BlobId == plan.Chunk.BlobId), 409, "Capture changed while its audio was being prepared.");
            var session = s.Sessions[plan.Chunk.RecordingId]; var recording = session.View;
            if (!plan.Existing) {
                s.Chunks.Add(plan.Chunk.Id, plan.Chunk);
                recording = recording with { AudioBytes = recording.AudioBytes + audio.Length, DurationSamples = Math.Max(recording.DurationSamples,
                    request.Sequence!.Value * RecordingProtocol.ChunkSeconds * RecordingProtocol.SampleRate + samples), Revision = recording.Revision + 1 };
            } else if (request.Discontinuous && !session.Discontinuous)
                recording = recording with { Status = recording.Status == "Complete" ? "Incomplete" : recording.Status, Revision = recording.Revision + 1 };
            session = session with { View = recording, Discontinuous = session.Discontinuous || request.Discontinuous };
            if (request.Discontinuous) session = RecordingTranscriptService.CaptureBecameIncomplete(session, runtime);
            s.Sessions[recording.Id] = session; return Task.FromResult(Result(s, recording));
        }, async (plan, outcome) => {
            if (!plan.Existing && outcome == RecordingCommitOutcome.Rejected) await files.DeleteAsync(plan.Chunk, CancellationToken.None);
        }, ct);
    private RecordingCaptureResult Result(RecordingState state, InterviewRecording recording) => new(files.Ready, [], recording with { Title = "", TranscriptId = null },
        state.Chunks.Values.Where(c => c.RecordingId == recording.Id).OrderBy(c => c.Track).ThenBy(c => c.Sequence).Select(c => new RecordingChunkAck(c.Track, c.Sequence, c.Sha256)).ToArray());
    internal static void ClearBlock(RuntimeState runtime, InterviewRecording recording)
    {
        if (runtime.Devices.TryGetValue(recording.DeviceId, out var device) && device.Recording?.RecordingId == recording.Id) runtime.Devices[recording.DeviceId] = device with { Recording = null };
    }
    public Task<bool> DeleteAsync(Guid id, int revision, CancellationToken ct = default) => store.UpdateAsync(async (s, runtime) => {
        if (!s.Sessions.TryGetValue(id, out var session)) return false;
        Guard.Require(session.View.Revision == revision, 409, "Recording changed. Reload before deleting.");
        var practiceSessions = runtime.VoiceCoachings.Values.Where(p => p.SourceRecordingId == id).Select(p => p.SessionId).ToArray();
        await RecordedPracticePrivacy.RedactSessionsAsync(runtime, s.UpdateSession, practiceSessions, clock.GetUtcNow());
        await files.DeleteRecordingAsync(id, ct);
        foreach (var chunk in s.Chunks.Values.Where(c => c.RecordingId == id).ToArray()) { await files.DeleteAsync(chunk, ct); s.Chunks.Remove(chunk.Id); }
        if (session.View.TranscriptId is { } transcript && runtime.ImportedDocuments.TryGetValue(transcript, out var document) && document.DeletedAt is null) ImportService.Purge(runtime, document, clock.GetUtcNow());
        RecordedPracticePrivacy.Purge(runtime, id, clock.GetUtcNow());
        ClearBlock(runtime, session.View); s.Sessions.Remove(id); return true;
    }, ct);
}
