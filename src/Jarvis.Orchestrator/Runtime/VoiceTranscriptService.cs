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
        turn.Role is "user" or "assistant" && turn.Text is { Length: > 0 and <= 8000 } &&
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
                return previous.IsVoice ? current : current with { Messages = current.Messages!.Select(m => m.RequestId == id ? m with { IsVoice = true, SpeechSeconds = speechSeconds } : m).ToArray() };
            }
            Guard.Require((current.Messages?.Count ?? 0) < 2000, 409, "Session transcript is full.");
            return current with { UpdatedAt = now, Messages = [.. current.Messages ?? [], new(id, role, text, now, true, speechSeconds)] };
        }, ct);
        Guard.Require(session is not null, 404, "Session not found.");
        var canonical = VoiceTranscriptRules.Turn(sessionId, session.Messages!.Single(m => m.RequestId == id));
        await runtime.UpdateAsync(s => { VoiceTranscriptRules.Publish(s, canonical); return true; }, ct);
    }
}

public sealed class MemoryVoiceRepairStore(InMemorySessionStore sessions, InMemoryRuntimeStore runtime, MemoryPrivacyBoundary boundary) : IVoiceRepairStore
{
    public Task<int> RepairTranscriptsAsync(CancellationToken ct) => boundary.Run(async () => {
        var known = await runtime.ReadAsync(s => s.VoiceTurns.Keys.ToHashSet(), ct);
        var missing = (await sessions.ListAsync(ct)).SelectMany(s => (s.Messages ?? []).Where(m => m.IsVoice && !known.Contains(m.RequestId)).Select(m => VoiceTranscriptRules.Turn(s.Id, m))).Take(100).ToArray();
        if (missing.Length == 0) return 0;
        return await runtime.UpdateAsync(s => {
            var count = 0; foreach (var turn in missing.Where(t => !s.RetiredSessions.Contains(t.SessionId) && !s.VoiceTurns.ContainsKey(t.Id))) { VoiceTranscriptRules.Publish(s, turn); count++; }
            return count;
        }, ct);
    });
}
