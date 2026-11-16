using Jarvis.Contracts;
using Jarvis.Orchestrator.Sessions;

namespace Jarvis.Orchestrator.Runtime;

public interface IVoiceRepairStore
{
    Task<int> RepairTranscriptsAsync(CancellationToken ct);
}

public static class VoiceTranscriptRules
{
    public static void Validate(VoiceTurn turn) => Guard.Require(turn.Id != Guid.Empty && turn.SessionId != Guid.Empty &&
        turn.Role is ChatRoles.User or ChatRoles.Assistant && turn.Text is { Length: > 0 and <= 8000 } &&
        (turn.SpeechSeconds is null || double.IsFinite(turn.SpeechSeconds.Value) && turn.SpeechSeconds is >= 0 and <= 180), 400, "Invalid bounded voice transcript.");
    public static void Publish(RuntimeState state, VoiceTurn turn)
    {
        Validate(turn); Guard.Require(!state.RetiredSessions.Contains(turn.SessionId), 409, "Session was retired.");
        if (state.VoiceTurns.TryGetValue(turn.Id, out var previous)) {
            Guard.Require(previous.SessionId == turn.SessionId && previous.Role == turn.Role && previous.Text == turn.Text && previous.SpeechSeconds == turn.SpeechSeconds,
                409, "Voice transcript ID was reused."); return;
        }
        state.VoiceTurns.Add(turn.Id, turn);
    }
    public static VoiceTurn Turn(Guid sessionId, ChatMessage message) => new(message.RequestId, sessionId, message.Role, message.Text, message.SpeechSeconds, message.At);
}

// Canonical transcript first, with a durable repair marker; a lost projection write never replays AI.
public sealed class VoiceTranscriptService(ISessionStore sessions, IRuntimeStore runtime, TimeProvider clock)
{
    public async Task RecordAsync(Guid sessionId, Guid id, string role, string text, double? speechSeconds, CancellationToken ct)
    {
        var now = clock.GetUtcNow(); now = new(now.UtcTicks - now.UtcTicks % 10, TimeSpan.Zero);
        var turn = new VoiceTurn(id, sessionId, role, text, speechSeconds, now); VoiceTranscriptRules.Validate(turn);
        var session = await sessions.UpdateAsync(sessionId, current => {
            var previous = current.Messages?.SingleOrDefault(m => m.RequestId == id);
            if (previous is not null) {
                Guard.Require(previous.Role == role && previous.Text == text && (!previous.IsVoice || previous.SpeechSeconds == speechSeconds), 409, "Transcript ID was reused.");
                return current with {
                    Messages = previous.IsVoice ? current.Messages : current.Messages!.Select(m => m.RequestId == id ? m with { IsVoice = true, SpeechSeconds = speechSeconds } : m).ToArray(),
                    PendingVoiceProjectionIds = (current.PendingVoiceProjectionIds ?? []).Append(id).Distinct().ToArray()
                };
            }
            Guard.Require((current.Messages?.Count ?? 0) < 2000, 409, "Session transcript is full.");
            return current with { UpdatedAt = now, Messages = [.. current.Messages ?? [], new(id, role, text, now, true, speechSeconds)],
                PendingVoiceProjectionIds = (current.PendingVoiceProjectionIds ?? []).Append(id).Distinct().ToArray() };
        }, ct);
        Guard.Require(session is not null, 404, "Session not found.");
        var canonical = VoiceTranscriptRules.Turn(sessionId, session.Messages!.Single(m => m.RequestId == id));
        await runtime.UpdateAsync(s => { VoiceTranscriptRules.Publish(s, canonical); return true; }, ct);
        await sessions.UpdateAsync(sessionId, current => current with { PendingVoiceProjectionIds = (current.PendingVoiceProjectionIds ?? []).Where(pending => pending != id).ToArray() }, ct);
    }
}

public sealed class MemoryVoiceRepairStore(ISessionStore sessions, InMemoryRuntimeStore runtime, MemoryPrivacyBoundary boundary) : IVoiceRepairStore
{
    public Task<int> RepairTranscriptsAsync(CancellationToken ct) => boundary.Run(async () => {
        var pending = await sessions.ListPendingVoiceProjectionsAsync(100, ct);
        if (pending.Count == 0) return 0;
        var turns = new List<VoiceTurn>();
        foreach (var entry in pending) {
            var session = await sessions.GetAsync(entry.SessionId, ct);
            if (session is null) continue;
            var ids = entry.RequestIds.ToHashSet();
            turns.AddRange((session.Messages ?? []).Where(m => ids.Contains(m.RequestId) && m.IsVoice).Select(m => VoiceTranscriptRules.Turn(session.Id, m)));
        }
        var written = turns.Count == 0 ? 0 : await runtime.UpdateAsync(s => {
            var count = 0; foreach (var turn in turns.Where(t => !s.RetiredSessions.Contains(t.SessionId) && !s.VoiceTurns.ContainsKey(t.Id))) { VoiceTranscriptRules.Publish(s, turn); count++; }
            return count;
        }, ct);
        foreach (var entry in pending) {
            var ids = entry.RequestIds.ToHashSet();
            await sessions.UpdateAsync(entry.SessionId, current => current with { PendingVoiceProjectionIds = (current.PendingVoiceProjectionIds ?? []).Where(id => !ids.Contains(id)).ToArray() }, ct);
        }
        return written;
    });
}
