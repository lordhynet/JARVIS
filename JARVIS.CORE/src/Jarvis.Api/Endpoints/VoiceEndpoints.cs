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
        app.MapPost(CoreRoutes.VoiceHandoff, (CompanionHandoffRequest request, HttpContext context,
            Jarvis.Orchestrator.Cqrs.ICommandHandler<Jarvis.Orchestrator.Cqrs.Sessions.ControlCompanionHandoff, CompanionHandoffView> handler, CancellationToken ct) => {
            ApiKeyMiddleware.RequireDevice(context, request.DeviceId); context.Response.Headers.CacheControl = "no-store";
            return handler.HandleAsync(new(request), ct);
        }).RequireAuthorization(CorePolicies.OwnerOrCompanion);
        app.MapPost(CoreRoutes.VoiceCalibration, (VoiceStartRequest request, HttpContext context, AudioOwnership ownership, CancellationToken ct) => {
            ApiKeyMiddleware.RequireDevice(context, request.DeviceId); return ownership.StartCalibrationAsync(request.DeviceId, ct);
        }).RequireAuthorization(CorePolicies.Owner);
        app.MapPost(CoreRoutes.VoiceLocal, (VoiceStartRequest request, HttpContext context, AudioOwnership ownership, CancellationToken ct) => {
            ApiKeyMiddleware.RequireDevice(context, request.DeviceId); return ownership.StartLocalAsync(request.SessionId, request.DeviceId, ct);
        }).RequireAuthorization(CorePolicies.Owner);
        app.MapPost(CoreRoutes.VoiceLocalRenew, (AudioLease lease, HttpContext context, AudioOwnership ownership, CancellationToken ct) => {
            ApiKeyMiddleware.RequireDevice(context, lease.DeviceId); return ownership.RenewAsync(lease, ct);
        }).RequireAuthorization(CorePolicies.Owner);
        app.MapPost(CoreRoutes.VoiceLocalRelease, (AudioLease lease, HttpContext context, AudioOwnership ownership, CancellationToken ct) => {
            ApiKeyMiddleware.RequireDevice(context, lease.DeviceId); return ownership.ReleaseAsync(lease, ct);
        }).RequireAuthorization(CorePolicies.OwnerOrCompanion);
        app.MapGet(CoreRoutes.VoiceStatus, (ManagedAi ai) => ai.Status("Voice")).RequireAuthorization(CorePolicies.OwnerOrCompanion);
        app.MapGet(CoreRoutes.VoiceModes, (ActivityType? activity, TrainingMode? drill, ManagedAi ai) =>
            ai.VoiceOptions(activity ?? ActivityType.General, drill)).RequireAuthorization(CorePolicies.Owner);
        app.MapPost(CoreRoutes.VoicePrepare, (CompanionVoiceRequest request, HttpContext context, ManagedAi ai, CompanionVoiceService voice, CancellationToken ct) =>
        { ApiKeyMiddleware.RequireDevice(context, request.DeviceId); ai.RequireReady("Voice"); return voice.PrepareAsync(request, ct); }).RequireRateLimiting("voice").RequireAuthorization(CorePolicies.OwnerOrCompanion);
        app.MapPost(CoreRoutes.VoiceWakeCandidates, async (WakeCandidateRequest request, HttpContext context, ManagedAi ai, WakeCandidateArbiter arbiter, CancellationToken ct) => {
            ApiKeyMiddleware.RequireDevice(context, request.DeviceId); ai.RequireReady("Voice"); return await arbiter.ClaimAsync(request, ct, ai.RealtimeWindow().Seconds);
        }).RequireRateLimiting("voice").RequireAuthorization(CorePolicies.OwnerOrCompanion);
        app.MapPost(CoreRoutes.VoiceTickets, (VoiceStartRequest request, HttpContext context, ManagedAi ai, AudioOwnership ownership, CancellationToken ct) =>
        { ApiKeyMiddleware.RequireDevice(context, request.DeviceId); ai.RequireReady("Voice"); return ownership.StartAsync(request, ct, ai.RealtimeWindow().Seconds); }).RequireRateLimiting("voice").RequireAuthorization(CorePolicies.OwnerOrCompanion);
        // No reusable key in a URL. Authentication is a single-use 15-second ticket in the first frame.
        app.Map(CoreRoutes.VoiceStream, (HttpContext context, RealtimeGateway gateway) => gateway.RunAsync(context)).AllowAnonymous();
    }
}
