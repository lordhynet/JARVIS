using Jarvis.Contracts;
using Jarvis.Api.Integrations;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Cqrs;
using Jarvis.Orchestrator.Cqrs.Imports;

namespace Jarvis.Api.Endpoints;

public static class ImportEndpoints
{
    public static void MapImportEndpoints(this WebApplication app)
    {
        app.MapPost("/api/imports/{id:guid}/assess", (Guid id, AiAssessmentRequest request, AiCoach coach, CancellationToken ct) => coach.ImportedAsync(id, request, ct)).RequireRateLimiting("cloud");
        app.MapGet("/api/imports/{id:guid}/assessments", (Guid id, IRuntimeReader reader, CancellationToken ct) => reader.ReadAsync(s => s.AiCoachAssessments.Values.Where(x => x.AttemptId == id).ToList(), ct));
        app.MapGet("/api/imports", (IRuntimeReader reader, CancellationToken ct) => reader.ReadAsync(s => s.ImportedDocuments.Values
            .Where(x => x.DeletedAt is null).OrderByDescending(x => x.ImportedAt).Select(x => new { x.Id, x.Kind, x.Title, x.Revision, x.ImportedAt, Characters = x.Text.Length }).ToList(), ct));
        app.MapGet("/api/imports/{id:guid}", (Guid id, IRuntimeReader reader, CancellationToken ct) => reader.ReadAsync(s =>
        {
            Guard.Require(s.ImportedDocuments.TryGetValue(id, out var document) && document.DeletedAt is null, 404, "Import not found."); return document;
        }, ct));
        app.MapPost("/api/imports", (ImportDocumentRequest request, ICommandHandler<ImportDocument, ImportedDocument> handler, CancellationToken ct) => handler.HandleAsync(new(request), ct));
        app.MapDelete("/api/imports/{id:guid}", (Guid id, int revision, ICommandHandler<DeleteImport, bool> handler, CancellationToken ct) => handler.HandleAsync(new(id, revision), ct));
        app.MapPost("/api/imports/{id:guid}/claims", (Guid id, AttachSourceClaimRequest request, ICommandHandler<AttachImportClaim, SourceClaim> handler, CancellationToken ct) => handler.HandleAsync(new(id, request), ct));
        app.MapGet("/api/imports/{id:guid}/claims", (Guid id, IRuntimeReader reader, CancellationToken ct) => reader.ReadAsync(s => s.SourceClaims.Values.Where(x => x.DocumentId == id).ToList(), ct));
        app.MapGet("/api/imports/{id:guid}/review", (Guid id, IRuntimeReader reader, CancellationToken ct) => reader.ReadAsync(s =>
        {
            Guard.Require(s.ImportedDocuments.TryGetValue(id, out var document), 404, "Import not found."); return ImportService.Review(document);
        }, ct));
    }
}
