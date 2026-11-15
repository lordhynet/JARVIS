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
        app.MapPost("/api/imports/{id:guid}/assess", (Guid id, AiAssessmentRequest request, ICommandHandler<RequestImportAiAssessment, AiCoachAssessment> handler, CancellationToken ct) => handler.HandleAsync(new(id, request), ct)).RequireRateLimiting("cloud");
        app.MapGet("/api/imports/{id:guid}/assessments", (Guid id, IQueryHandler<ListImportAssessments, IReadOnlyList<AiCoachAssessment>> handler, CancellationToken ct) => handler.HandleAsync(new(id), ct));
        app.MapGet("/api/imports", (IQueryHandler<ListImports, IReadOnlyList<ImportedDocumentSummary>> handler, CancellationToken ct) => handler.HandleAsync(new(), ct));
        app.MapGet("/api/imports/{id:guid}", (Guid id, IQueryHandler<GetImport, ImportedDocument> handler, CancellationToken ct) => handler.HandleAsync(new(id), ct));
        app.MapPost("/api/imports", (ImportDocumentRequest request, ICommandHandler<ImportDocument, ImportedDocument> handler, CancellationToken ct) => handler.HandleAsync(new(request), ct));
        app.MapDelete("/api/imports/{id:guid}", (Guid id, int revision, ICommandHandler<DeleteImport, bool> handler, CancellationToken ct) => handler.HandleAsync(new(id, revision), ct));
        app.MapPost("/api/imports/{id:guid}/claims", (Guid id, AttachSourceClaimRequest request, ICommandHandler<AttachImportClaim, SourceClaim> handler, CancellationToken ct) => handler.HandleAsync(new(id, request), ct));
        app.MapGet("/api/imports/{id:guid}/claims", (Guid id, IQueryHandler<ListImportClaims, IReadOnlyList<SourceClaim>> handler, CancellationToken ct) => handler.HandleAsync(new(id), ct));
        app.MapGet("/api/imports/{id:guid}/review", (Guid id, IQueryHandler<GetImportReview, InterviewArchiveReview> handler, CancellationToken ct) => handler.HandleAsync(new(id), ct));
    }
}
