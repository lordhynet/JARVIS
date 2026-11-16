using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Cqrs.Imports;

public sealed record ImportDocument(ImportDocumentRequest Request) : ICommand<ImportedDocument>;
public sealed record DeleteImport(Guid Id, int Revision) : ICommand<bool>;
public sealed record AttachImportClaim(Guid DocumentId, AttachSourceClaimRequest Request) : ICommand<SourceClaim>;
public sealed record ListImports : IQuery<IReadOnlyList<ImportedDocumentSummary>>;
public sealed record GetImport(Guid Id) : IQuery<ImportedDocument>;
public sealed record ListImportClaims(Guid DocumentId) : IQuery<IReadOnlyList<SourceClaim>>;
public sealed record ListImportAssessments(Guid DocumentId) : IQuery<IReadOnlyList<AiCoachAssessment>>;
public sealed record GetImportReview(Guid Id) : IQuery<InterviewArchiveReview>;

public sealed class ImportCommandHandlers(ImportService imports) :
    ICommandHandler<ImportDocument, ImportedDocument>, ICommandHandler<DeleteImport, bool>, ICommandHandler<AttachImportClaim, SourceClaim>
{
    public Task<ImportedDocument> HandleAsync(ImportDocument command, CancellationToken ct = default) => imports.ImportAsync(command.Request, ct);
    public Task<bool> HandleAsync(DeleteImport command, CancellationToken ct = default) => imports.DeleteAsync(command.Id, command.Revision, ct);
    public Task<SourceClaim> HandleAsync(AttachImportClaim command, CancellationToken ct = default) => imports.AttachClaimAsync(command.DocumentId, command.Request, ct);
}

public sealed class ImportQueryHandlers(IRuntimeReader reader) :
    IQueryHandler<ListImports, IReadOnlyList<ImportedDocumentSummary>>, IQueryHandler<GetImport, ImportedDocument>,
    IQueryHandler<ListImportClaims, IReadOnlyList<SourceClaim>>, IQueryHandler<ListImportAssessments, IReadOnlyList<AiCoachAssessment>>,
    IQueryHandler<GetImportReview, InterviewArchiveReview>
{
    public Task<IReadOnlyList<ImportedDocumentSummary>> HandleAsync(ListImports query, CancellationToken ct = default) =>
        reader.ReadAsync<IReadOnlyList<ImportedDocumentSummary>>(s => s.ImportedDocuments.Values
            .Where(x => x.DeletedAt is null).OrderByDescending(x => x.ImportedAt)
            .Select(x => new ImportedDocumentSummary(x.Id, x.Kind, x.Title, x.Revision, x.ImportedAt, x.Text.Length)).ToList(), ct);

    public Task<ImportedDocument> HandleAsync(GetImport query, CancellationToken ct = default) => reader.ReadAsync(s =>
    {
        Guard.Require(s.ImportedDocuments.TryGetValue(query.Id, out var document) && document.DeletedAt is null, 404, "Import not found.");
        return document;
    }, ct);

    public Task<IReadOnlyList<SourceClaim>> HandleAsync(ListImportClaims query, CancellationToken ct = default) =>
        reader.ReadAsync<IReadOnlyList<SourceClaim>>(s => s.SourceClaims.Values.Where(x => x.DocumentId == query.DocumentId).ToList(), ct);

    public Task<IReadOnlyList<AiCoachAssessment>> HandleAsync(ListImportAssessments query, CancellationToken ct = default) =>
        reader.ReadAsync<IReadOnlyList<AiCoachAssessment>>(s => s.AiCoachAssessments.Values.Where(x => x.AttemptId == query.DocumentId).ToList(), ct);

    public Task<InterviewArchiveReview> HandleAsync(GetImportReview query, CancellationToken ct = default) => reader.ReadAsync(s =>
    {
        Guard.Require(s.ImportedDocuments.TryGetValue(query.Id, out var document), 404, "Import not found.");
        return ImportService.Review(document);
    }, ct);
}
