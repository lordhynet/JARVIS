using Jarvis.Api.Security;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Cqrs;
using Jarvis.Orchestrator.Cqrs.Integrations;
using Jarvis.Orchestrator.Recordings;

namespace Jarvis.Api.Endpoints;

public sealed record DeleteRecordingRequest(int Revision, bool? Locked);
public static class RecordingEndpoints
{
    public static void MapRecordingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/recordings").WithTags("Interview recordings").RequireAuthorization(CorePolicies.Owner);
        group.MapGet("/status", (RecordingService recordings) => new { ready = recordings.Ready, consent = RecordingProtocol.Consent, audioDays = 30, maximumSeconds = RecordingProtocol.MaximumSeconds });
        group.MapGet("/", (bool? locked, IQueryHandler<ListRecordings, IReadOnlyList<InterviewRecording>> handler, CancellationToken ct) => handler.HandleAsync(new(locked), ct));
        group.MapPost("/", (PrepareRecordingRequest request, ICommandHandler<PrepareRecording, InterviewRecording> handler, CancellationToken ct) => handler.HandleAsync(new(request), ct)).RequireAuthorization(CorePolicies.DeviceManage);
        group.MapPost("/{id:guid}/delete", (Guid id, DeleteRecordingRequest request, ICommandHandler<DeleteRecording, bool> handler, CancellationToken ct) => handler.HandleAsync(new(id, request.Revision, request.Locked), ct)).RequireAuthorization(CorePolicies.PrivacyManage);
        app.MapPost(RecordingProtocol.Capture, (RecordingCaptureRequest request, HttpContext context, ICommandHandler<CaptureRecording, RecordingCaptureResult> handler, CancellationToken ct) => {
            ApiKeyMiddleware.RequireDevice(context, request.DeviceId); return handler.HandleAsync(new(request), ct);
        }).RequireAuthorization(CorePolicies.OwnerOrCompanion);
    }
}
