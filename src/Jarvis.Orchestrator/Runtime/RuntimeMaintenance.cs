namespace Jarvis.Orchestrator.Runtime;

public static class RuntimeMaintenance
{
    // Only disposable caches/expired delivery fences. Never remove financial commitments,
    // canonical sessions, answers, transcripts or action audit history here.
    public static int Prune(RuntimeState state, DateTimeOffset now)
    {
        var removed = 0;
        foreach (var id in state.ProviderResults.Values.Where(x => x.At < now.AddDays(-7)).Select(x => x.Id).ToList()) { state.ProviderResults.Remove(id); removed++; }
        foreach (var id in state.VoiceTickets.Where(x => x.Value.ExpiresAt <= now).Select(x => x.Key).ToList()) { state.VoiceTickets.Remove(id); removed++; }
        foreach (var id in state.HubTickets.Where(x => x.Value.ExpiresAt <= now).Select(x => x.Key).ToList()) { state.HubTickets.Remove(id); removed++; }
        foreach (var id in state.PushDeliveries.Values.Where(x => !state.AgentOutbox.TryGetValue(x.NotificationId, out var notice) || notice.ExpiresAt <= now)
            .Select(x => x.Id).ToList()) { state.PushDeliveries.Remove(id); removed++; }
        return removed;
    }
}
