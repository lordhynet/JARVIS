using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Cqrs.Imports;

public sealed record ImportDocument(ImportDocumentRequest Request) : ICommand<ImportedDocument>;
public sealed record DeleteImport(Guid Id, int Revision) : ICommand<bool>;
public sealed record AttachImportClaim(Guid DocumentId, AttachSourceClaimRequest Request) : ICommand<SourceClaim>;

public sealed class ImportCommandHandlers(ImportService imports) :
    ICommandHandler<ImportDocument, ImportedDocument>, ICommandHandler<DeleteImport, bool>, ICommandHandler<AttachImportClaim, SourceClaim>
{
    public Task<ImportedDocument> HandleAsync(ImportDocument command, CancellationToken ct = default) => imports.ImportAsync(command.Request, ct);
    public Task<bool> HandleAsync(DeleteImport command, CancellationToken ct = default) => imports.DeleteAsync(command.Id, command.Revision, ct);
    public Task<SourceClaim> HandleAsync(AttachImportClaim command, CancellationToken ct = default) => imports.AttachClaimAsync(command.DocumentId, command.Request, ct);
}
