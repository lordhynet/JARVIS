using Jarvis.Contracts;

namespace Jarvis.Orchestrator.Runtime;

/// <summary>Core serializes wake responses through its existing global audio lease fence.</summary>
public sealed class WakeCandidateArbiter(CompanionVoiceService voice, AudioOwnership ownership)
{
    public async Task<WakeCandidateResponse> ClaimAsync(WakeCandidateRequest request, CancellationToken ct)
    {
        Guard.Require(request.RequestId != Guid.Empty && request.AllowCloudProcessing, 400,
            "A unique request and prior live-voice cloud consent are required after local wake detection.");
        var prepared = await voice.PrepareAsync(new(request.RequestId, request.DeviceId, true), ct);
        var sessionId = prepared.SessionId;
        var ticket = await ownership.StartAsync(new(sessionId, request.DeviceId, true), ct);
        return new(sessionId, ticket);
    }
}
