using Jarvis.Contracts;
using Jarvis.Orchestrator.Intelligence;

namespace Jarvis.Orchestrator.Runtime;

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

public sealed class VoiceTranscriptService(ICoreTransaction transaction, TimeProvider clock)
{
    public Task RecordAsync(Guid sessionId, Guid id, string role, string text, double? speechSeconds, CancellationToken ct) =>
        RecordAsync(sessionId, id, role, text, speechSeconds, clock.GetUtcNow(), null, ct);

    public Task RecordFallbackAsync(AudioLease lease, Guid captureId, DateTimeOffset promptedAt, DateTimeOffset capturedAt,
        string text, CancellationToken ct)
    {
        Guard.Require(capturedAt > promptedAt && capturedAt <= clock.GetUtcNow() && VoiceModePolicy.ConfirmsEveryday(text),
            409, "A fresh spoken yes is required to continue in Everyday voice.");
        return RecordAsync(lease.SessionId, captureId, ChatRoles.User, text, null, capturedAt, lease, ct);
    }

    private async Task RecordAsync(Guid sessionId, Guid id, string role, string text, double? speechSeconds, DateTimeOffset at,
        AudioLease? fallbackLease, CancellationToken ct)
    {
        at = new(at.UtcTicks - at.UtcTicks % 10, TimeSpan.Zero);
        var turn = new VoiceTurn(id, sessionId, role, text, speechSeconds, at);
        VoiceTranscriptRules.Validate(turn);
        await transaction.ExecuteAsync(async writes =>
        {
            if (fallbackLease is not null)
            {
                var state = writes.Runtime;
                Guard.Require(AudioFenceChanges.Matches(fallbackLease, state.AudioLease) && state.AudioLease!.ExpiresAt > clock.GetUtcNow() &&
                    !state.AgentControls.EffectivePaidAiPaused, 409, "Audio ownership changed before confirming Everyday voice.");
                Guard.Require(!state.VoiceTurns.ContainsKey(id), 409, "This spoken confirmation was already used.");
            }
            var session = await writes.UpdateSessionAsync(sessionId, current =>
            {
                if (fallbackLease is not null)
                    Guard.Require(current.Status == SessionStatus.Active && current.ActiveDeviceId == fallbackLease.DeviceId,
                        409, "Session audio ownership changed.");
                var previous = current.Messages?.SingleOrDefault(m => m.RequestId == id);
                if (previous is not null)
                {
                    Guard.Require(fallbackLease is null, 409, "This spoken confirmation was already used.");
                    Guard.Require(previous.Role == role && previous.Text == text && (!previous.IsVoice || previous.SpeechSeconds == speechSeconds),
                        409, "Transcript ID was reused.");
                    return previous.IsVoice ? current : VoiceConversationMemory.RefreshSummary(current with
                    {
                        Messages = current.Messages!.Select(m => m.RequestId == id ? m with { IsVoice = true, SpeechSeconds = speechSeconds } : m).ToArray()
                    });
                }
                Guard.Require((current.Messages?.Count ?? 0) < 2000, 409, "Session transcript is full.");
                return VoiceConversationMemory.RefreshSummary(current with
                {
                    UpdatedAt = current.UpdatedAt > at ? current.UpdatedAt : at,
                    Messages = [.. current.Messages ?? [], new(id, role, text, at, true, speechSeconds)]
                });
            });
            Guard.Require(session is not null, 404, "Session not found.");
            var canonical = VoiceTranscriptRules.Turn(sessionId, session.Messages!.Single(m => m.RequestId == id));
            VoiceTranscriptRules.Publish(writes.Runtime, canonical);
            return true;
        }, ct);
    }
}
