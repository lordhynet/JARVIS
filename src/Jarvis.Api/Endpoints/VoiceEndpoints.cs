using Jarvis.Api.Integrations;
using Jarvis.Api.Voice;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Api.Security;

namespace Jarvis.Api.Endpoints;

public static class VoiceEndpoints
{
    public static void MapVoiceEndpoints(this WebApplication app)
    {
        app.MapPost("/api/voice/calibrate", (VoiceStartRequest request, HttpContext context, AudioOwnership ownership, CancellationToken ct) => {
            ApiKeyMiddleware.RequireDevice(context, request.DeviceId); return ownership.StartCalibrationAsync(request.DeviceId, ct);
        }).RequireAuthorization(CorePolicies.Owner);
        app.MapPost("/api/voice/local", (VoiceStartRequest request, HttpContext context, AudioOwnership ownership, CancellationToken ct) => {
            ApiKeyMiddleware.RequireDevice(context, request.DeviceId); return ownership.StartLocalAsync(request.SessionId, request.DeviceId, ct);
        }).RequireAuthorization(CorePolicies.Owner);
        app.MapPost("/api/voice/local/renew", (AudioLease lease, HttpContext context, AudioOwnership ownership, CancellationToken ct) => {
            ApiKeyMiddleware.RequireDevice(context, lease.DeviceId); return ownership.RenewAsync(lease, ct);
        }).RequireAuthorization(CorePolicies.Owner);
        app.MapPost("/api/voice/local/release", (AudioLease lease, HttpContext context, AudioOwnership ownership, CancellationToken ct) => {
            ApiKeyMiddleware.RequireDevice(context, lease.DeviceId); return ownership.ReleaseAsync(lease, ct);
        }).RequireAuthorization(CorePolicies.OwnerOrCompanion);
        app.MapGet("/api/voice/status", (ManagedAi ai) => ai.Status("Voice")).RequireAuthorization(CorePolicies.OwnerOrCompanion);
        app.MapPost("/api/voice/prepare", (CompanionVoiceRequest request, HttpContext context, ManagedAi ai, CompanionVoiceService voice, CancellationToken ct) =>
        { ApiKeyMiddleware.RequireDevice(context, request.DeviceId); ai.RequireReady("Voice"); return voice.PrepareAsync(request, ct); }).RequireRateLimiting("voice").RequireAuthorization(CorePolicies.OwnerOrCompanion);
        app.MapPost("/api/voice/wake-candidates", async (WakeCandidateRequest request, HttpContext context, ManagedAi ai, WakeCandidateArbiter arbiter, CancellationToken ct) => {
            ApiKeyMiddleware.RequireDevice(context, request.DeviceId); ai.RequireReady("Voice"); return await arbiter.ClaimAsync(request, ct);
        }).RequireRateLimiting("voice").RequireAuthorization(CorePolicies.OwnerOrCompanion);
        app.MapPost("/api/voice/tickets", (VoiceStartRequest request, HttpContext context, ManagedAi ai, AudioOwnership ownership, CancellationToken ct) =>
        { ApiKeyMiddleware.RequireDevice(context, request.DeviceId); ai.RequireReady("Voice"); return ownership.StartAsync(request, ct); }).RequireRateLimiting("voice").RequireAuthorization(CorePolicies.OwnerOrCompanion);
        // No reusable key in a URL. Authentication is a single-use 15-second ticket in the first frame.
        app.Map("/voice-stream", (HttpContext context, RealtimeGateway gateway) => gateway.RunAsync(context)).RequireRateLimiting("voice").AllowAnonymous();
    }
}
