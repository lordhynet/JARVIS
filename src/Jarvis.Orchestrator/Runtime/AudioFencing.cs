namespace Jarvis.Orchestrator.Runtime;

public static class AudioFencing
{
    public static void Revoke(RuntimeState state)
    {
        if (state.AudioLease is not { } old) return;
        state.AudioDrainLease = old; state.AudioDrainUntil = old.ExpiresAt; state.AudioLease = null;
    }
    public static void RequireAvailable(RuntimeState state, DateTimeOffset now)
    {
        Guard.Require((state.AudioLease is null || state.AudioLease.ExpiresAt <= now) &&
            (state.AudioDrainUntil is null || state.AudioDrainUntil <= now), 409,
            "Another audio lease is active or draining. Stop the source device or wait for its lease to expire.");
        state.AudioDrainLease = null; state.AudioDrainUntil = null;
    }
}
