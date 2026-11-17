using Jarvis.Contracts;

namespace Jarvis.Orchestrator.Runtime;

public sealed record RuntimeAudioLeaseState(AudioLease? Lease, AudioLease? DrainingLease, DateTimeOffset? DrainUntil, long NextFence)
{
    public static RuntimeAudioLeaseState From(RuntimeState state) =>
        new(state.AudioLease, state.AudioDrainLease, state.AudioDrainUntil, state.NextAudioFence);

    public void Apply(RuntimeState state)
    {
        state.AudioLease = Lease;
        state.AudioDrainLease = DrainingLease;
        state.AudioDrainUntil = DrainUntil;
        state.NextAudioFence = NextFence;
    }

    public RuntimeAudioLeaseState Release(AudioLease lease)
    {
        if (Lease is { } current && AudioFenceChanges.Matches(lease, current))
            return this with { Lease = null };
        if (DrainingLease is { } draining && AudioFenceChanges.Matches(lease, draining))
            return this with { DrainingLease = null, DrainUntil = null };
        return this;
    }
}

public static class AudioFencing
{
    public static void Revoke(RuntimeState state)
    {
        if (state.AudioLease is not { } old) return;
        state.AudioDrainLease = old; state.AudioDrainUntil = old.ExpiresAt; state.AudioLease = null;
    }
    public static void RequireAvailable(RuntimeState state, DateTimeOffset now)
    {
        var audio = new AudioLeaseState(state.AudioLease, state.AudioDrainUntil);
        Guard.Require(audio.IsAvailable(now), 409,
            "Another audio lease is active or draining. Stop the source device or wait for its lease to expire.");
        state.AudioDrainLease = null; state.AudioDrainUntil = null;
    }
}
