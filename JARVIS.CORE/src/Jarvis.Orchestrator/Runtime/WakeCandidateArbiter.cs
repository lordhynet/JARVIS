using Jarvis.Contracts;

namespace Jarvis.Orchestrator.Runtime;

/// <summary>Core serializes wake responses through its existing global audio lease fence.</summary>
public static class WakeCandidateRules
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    public const int MaximumPerDevice = 6;
    private const int MaximumTrackedDevices = 1000;

    public static void RecordAttempt(RuntimeState state, string deviceId, DateTimeOffset now)
    {
        var cutoff = now - Window;
        foreach (var key in state.WakeCandidateAttempts.Keys.ToArray())
        {
            var recent = state.WakeCandidateAttempts[key].Where(at => at > cutoff).ToList();
            if (recent.Count == 0) state.WakeCandidateAttempts.Remove(key);
            else state.WakeCandidateAttempts[key] = recent;
        }
        if (!state.WakeCandidateAttempts.TryGetValue(deviceId, out var attempts))
        {
            Guard.Require(state.WakeCandidateAttempts.Count < MaximumTrackedDevices, 503, "Wake candidate tracking capacity is full.");
            attempts = [];
            state.WakeCandidateAttempts.Add(deviceId, attempts);
        }
        if (attempts.Count >= MaximumPerDevice)
        {
            CoreTelemetry.WakeCandidates.Add(1, new KeyValuePair<string, object?>("outcome", "rate_limited"));
            throw new DomainException(429, "This device has reached the local wake limit. Try again after the ten-minute window.");
        }
        attempts.Add(now);
        CoreTelemetry.WakeCandidates.Add(1, new KeyValuePair<string, object?>("outcome", "accepted"));
    }
}

public sealed class WakeCandidateArbiter(CompanionVoiceService voice, AudioOwnership ownership, IRuntimeStore runtime, TimeProvider clock)
{
    public async Task<WakeCandidateResponse> ClaimAsync(WakeCandidateRequest request, CancellationToken ct,
        int windowSeconds = VoiceWindowLimits.DefaultSeconds)
    {
        Guard.Require(request.RequestId != Guid.Empty && request.AllowCloudProcessing, 400,
            "A unique request and prior live-voice cloud consent are required after local wake detection.");
        await runtime.UpdateAsync(state => { WakeCandidateRules.RecordAttempt(state, request.DeviceId, clock.GetUtcNow()); return true; }, ct);
        return await voice.PrepareWakeAsync(new(request.RequestId, request.DeviceId, true, request.Locked), ownership, windowSeconds, ct);
    }
}
