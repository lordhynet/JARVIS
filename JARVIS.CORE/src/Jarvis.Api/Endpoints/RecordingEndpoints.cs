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
        group.AddEndpointFilter(async (context, next) => { context.HttpContext.Response.Headers.CacheControl = "no-store"; return await next(context); });
        group.MapGet("/{id:guid}/practice", (Guid id, bool? locked, IQueryHandler<GetRecordedPractice, RecordedPracticePage> handler, CancellationToken ct) => handler.HandleAsync(new(id, locked), ct));
        group.MapPost("/{id:guid}/practice", (Guid id, StartRecordedPractice request, ICommandHandler<BeginRecordedPractice, RecordedPracticeResult> handler, CancellationToken ct) => handler.HandleAsync(new(id, request), ct));
        group.MapPost("/{id:guid}/practice/cards", (Guid id, ApproveRecordedCard request, ICommandHandler<ApproveRecordedPracticeCard, LearningItem> handler, CancellationToken ct) => handler.HandleAsync(new(id, request), ct));
        group.MapGet("/{id:guid}/practice/{question:guid}/audio/{piece:guid}", async (Guid id, Guid question, Guid piece, int revision, bool? locked,
            HttpContext context, IQueryHandler<GetRecordedClip, byte[]> handler, CancellationToken ct) => {
            var bytes = await handler.HandleAsync(new(id, question, piece, revision, locked), ct);
            try { context.Response.ContentType = "audio/wav"; context.Response.ContentLength = bytes.Length;
                context.Response.Headers.ContentDisposition = "inline"; context.Response.Headers["X-Content-Type-Options"] = "nosniff";
                await context.Response.Body.WriteAsync(bytes, ct);
            } finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
        }).RequireAuthorization(CorePolicies.PrivacyManage);
        group.MapGet("/status", (RecordingService recordings) => new { ready = recordings.Ready, consent = RecordingProtocol.Consent, audioDays = 30, maximumSeconds = RecordingProtocol.MaximumSeconds });
        group.MapGet("/", (bool? locked, IQueryHandler<ListRecordings, IReadOnlyList<InterviewRecording>> handler, CancellationToken ct) => handler.HandleAsync(new(locked), ct));
        group.MapPost("/", (PrepareRecordingRequest request, ICommandHandler<PrepareRecording, InterviewRecording> handler, CancellationToken ct) => handler.HandleAsync(new(request), ct)).RequireAuthorization(CorePolicies.DeviceManage);
        group.MapPost("/{id:guid}/delete", (Guid id, DeleteRecordingRequest request, ICommandHandler<DeleteRecording, bool> handler, CancellationToken ct) => handler.HandleAsync(new(id, request.Revision, request.Locked), ct)).RequireAuthorization(CorePolicies.PrivacyManage);
        group.MapGet("/{id:guid}/transcription", (Guid id, bool? locked, IQueryHandler<GetRecordingTranscript, RecordingTranscriptProgress?> handler, CancellationToken ct) => handler.HandleAsync(new(id, locked), ct));
        group.MapPost("/{id:guid}/transcription/quote", (Guid id, QuoteRecordingTranscriptRequest request, ICommandHandler<QuoteRecordingTranscript, RecordingTranscriptProgress> handler, CancellationToken ct) => handler.HandleAsync(new(id, request), ct));
        group.MapPost("/{id:guid}/transcription/advance", (Guid id, AdvanceRecordingTranscriptRequest request, ICommandHandler<AdvanceRecordingTranscript, RecordingTranscriptProgress> handler, CancellationToken ct) => handler.HandleAsync(new(id, request), ct));
        group.MapPost("/{id:guid}/transcription/speakers", (Guid id, RelabelRecordingSegmentRequest request, ICommandHandler<RelabelRecordingTranscript, ImportedDocument> handler, CancellationToken ct) => handler.HandleAsync(new(id, request), ct));
        app.MapPost(RecordingProtocol.Capture, (RecordingCaptureRequest request, HttpContext context, ICommandHandler<CaptureRecording, RecordingCaptureResult> handler, CancellationToken ct) => {
            ApiKeyMiddleware.RequireDevice(context, request.DeviceId); return handler.HandleAsync(new(request), ct);
        }).RequireAuthorization(CorePolicies.OwnerOrCompanion);
    }
}
