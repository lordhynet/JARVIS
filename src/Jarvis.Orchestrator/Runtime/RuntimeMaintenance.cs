namespace Jarvis.Orchestrator.Runtime;

public static class RuntimeMaintenance
{
    public static bool HasExpired(RuntimeState state, DateTimeOffset now) =>
        state.ProviderResults.Values.Any(x => x.At < now.AddDays(-7)) ||
        state.ProviderAttempts.Values.Any(x => x.Fingerprint.Length > 0 && x.CompletedAt < now.AddDays(-7)) ||
        state.VoiceTickets.Values.Any(x => x.ExpiresAt <= now) || state.HubTickets.Values.Any(x => x.ExpiresAt <= now) ||
        state.AiCoachAssessmentClaims.Values.Any(x => x.ExpiresAt <= now) ||
        state.AgentOutbox.Values.Any(x => x.ExpiresAt <= now) ||
        state.PushDeliveries.Values.Any(x => !state.AgentOutbox.TryGetValue(x.NotificationId, out var notice) || notice.ExpiresAt <= now);
    // Only disposable caches/expired delivery fences. Never remove financial commitments,
    // canonical sessions, answers, transcripts or action audit history here.
    public static int Prune(RuntimeState state, DateTimeOffset now)
    {
        var removed = 0;
        foreach (var attempt in state.ProviderAttempts.Values.Where(x => x.Fingerprint.Length > 0 && x.CompletedAt < now.AddDays(-7)).ToList())
            state.ProviderAttempts[attempt.Id] = attempt with { Fingerprint = "" };
        foreach (var id in state.ProviderResults.Values.Where(x => x.At < now.AddDays(-7)).Select(x => x.Id).ToList()) {
            state.ProviderResults.Remove(id);
            if (state.ProviderAttempts.TryGetValue(id, out var attempt)) state.ProviderAttempts[id] = attempt with { Fingerprint = "" };
            removed++;
        }
        foreach (var id in state.VoiceTickets.Where(x => x.Value.ExpiresAt <= now).Select(x => x.Key).ToList()) { state.VoiceTickets.Remove(id); removed++; }
        foreach (var id in state.HubTickets.Where(x => x.Value.ExpiresAt <= now).Select(x => x.Key).ToList()) { state.HubTickets.Remove(id); removed++; }
        foreach (var id in state.AiCoachAssessmentClaims.Where(x => x.Value.ExpiresAt <= now).Select(x => x.Key).ToList()) { state.AiCoachAssessmentClaims.Remove(id); removed++; }
        foreach (var id in state.AgentOutbox.Values.Where(x => x.ExpiresAt <= now).Select(x => x.Id).ToList()) { state.AgentOutbox.Remove(id); removed++; }
        foreach (var id in state.PushDeliveries.Values.Where(x => !state.AgentOutbox.TryGetValue(x.NotificationId, out var notice) || notice.ExpiresAt <= now)
            .Select(x => x.Id).ToList()) { state.PushDeliveries.Remove(id); removed++; }
        return removed;
    }
}
