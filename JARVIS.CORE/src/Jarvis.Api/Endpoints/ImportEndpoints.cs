using Jarvis.Contracts;
using Jarvis.Api.Integrations;
using Jarvis.Orchestrator.Cqrs;
using Jarvis.Orchestrator.Cqrs.Imports;
using Jarvis.Orchestrator.Cqrs.Coaching;

namespace Jarvis.Api.Endpoints;

public static class ImportEndpoints
{
    public static void MapImportEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/imports").WithTags("Imports").RequireAuthorization(Jarvis.Api.Security.CorePolicies.Owner);
        group.MapPost("/{id:guid}/assess", (Guid id, AiAssessmentRequest request, ICommandHandler<RequestImportAiAssessment, AiCoachAssessment> handler, CancellationToken ct) => handler.HandleAsync(new(id, request), ct)).RequireRateLimiting("cloud");
        group.MapGet("/{id:guid}/assessments", (Guid id, IQueryHandler<ListImportAssessments, IReadOnlyList<AiCoachAssessment>> handler, CancellationToken ct) => handler.HandleAsync(new(id), ct));
        group.MapGet("/", (IQueryHandler<ListImports, IReadOnlyList<ImportedDocumentSummary>> handler, CancellationToken ct) => handler.HandleAsync(new(), ct));
        group.MapGet("/{id:guid}", (Guid id, IQueryHandler<GetImport, ImportedDocument> handler, CancellationToken ct) => handler.HandleAsync(new(id), ct));
        group.MapPost("/", (ImportDocumentRequest request, ICommandHandler<ImportDocument, ImportedDocument> handler, CancellationToken ct) => handler.HandleAsync(new(request), ct));
        group.MapDelete("/{id:guid}", (Guid id, int revision, ICommandHandler<DeleteImport, bool> handler, CancellationToken ct) => handler.HandleAsync(new(id, revision), ct));
        group.MapPost("/{id:guid}/claims", (Guid id, AttachSourceClaimRequest request, ICommandHandler<AttachImportClaim, SourceClaim> handler, CancellationToken ct) => handler.HandleAsync(new(id, request), ct));
        group.MapGet("/{id:guid}/claims", (Guid id, IQueryHandler<ListImportClaims, IReadOnlyList<SourceClaim>> handler, CancellationToken ct) => handler.HandleAsync(new(id), ct));
        group.MapGet("/{id:guid}/review", (Guid id, IQueryHandler<GetImportReview, InterviewArchiveReview> handler, CancellationToken ct) => handler.HandleAsync(new(id), ct));
        // B3-5 v0: deterministic, read-only prep pack from a job advert; no AI, nothing stored.
        group.MapGet("/{id:guid}/prep-pack", (Guid id, IQueryHandler<GetImportPrepPack, InterviewPrepPack> handler, CancellationToken ct) => handler.HandleAsync(new(id), ct));
        // Owner action: selected prep-pack questions become review cards; repeats return the same cards.
        group.MapPost("/{id:guid}/prep-pack/cards", (Guid id, CreatePrepPackCardsRequest request, ICommandHandler<CreateImportPrepPackCards, PrepPackCards> handler, CancellationToken ct) => handler.HandleAsync(new(id, request), ct));
    }
}
