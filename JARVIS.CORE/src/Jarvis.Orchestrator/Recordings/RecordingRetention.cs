using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Recordings;

public static class RecordingRetention
{
    public static void CloseExpiredCaptures(RecordingState state, RuntimeState runtime, DateTimeOffset now)
    {
        foreach (var (id, session) in state.Sessions.ToArray()) {
            var invalidCredential = !runtime.DeviceCredentials.TryGetValue(session.View.DeviceId, out var credential) || credential.Revoked || credential.SecretHash != session.CredentialHash;
            if (session.View.Status != "Recording" || !invalidCredential && session.View.StartedAt > now.AddHours(-3)) continue;
            RecordingService.ClearBlock(runtime, session.View);
            state.Sessions[id] = session with { View = session.View with { Status = "Incomplete", StoppedAt = session.View.StoppedAt ?? now, Revision = session.View.Revision + 1 } };
        }
    }
    public static PrivacyCounts Counts(RecordingState state, PrivacyCounts counts, DateTimeOffset? asOf = null)
    {
        var ids = state.Sessions.Values.Where(r => asOf is null || !r.View.AudioDeleted && r.View.AudioRetainUntil <= asOf).Select(r => r.View.Id).ToHashSet();
        return counts with { InterviewRecordings = ids.Count, RecordingChunks = state.Chunks.Values.Count(c => ids.Contains(c.RecordingId)),
            RecordingAudioBytes = state.Chunks.Values.Where(c => ids.Contains(c.RecordingId)).Sum(c => c.Bytes) };
    }
    public static async Task ApplyAsync(RecordingState state, RuntimeState runtime, IRecordingAudioFiles? files, DateTimeOffset asOf, CancellationToken ct)
    {
        foreach (var (id, session) in state.Sessions.ToArray()) {
            if (session.View.AudioDeleted || !(session.View.AudioRetainUntil <= asOf)) continue;
            if (files is not null) await files.DeleteRecordingAsync(id, ct);
            foreach (var chunk in state.Chunks.Values.Where(c => c.RecordingId == id).ToArray()) state.Chunks.Remove(chunk.Id);
            RecordingService.ClearBlock(runtime, session.View);
            state.Sessions[id] = session with { View = session.View with { AudioDeleted = true, AudioBytes = 0, Status = session.View.Status == "Recording" ? "Incomplete" : session.View.Status, Revision = session.View.Revision + 1 } };
        }
    }
}
