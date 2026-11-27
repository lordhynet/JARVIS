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
public sealed record GetImportPrepPack(Guid Id) : IQuery<InterviewPrepPack>;

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
    IQueryHandler<GetImportReview, InterviewArchiveReview>, IQueryHandler<GetImportPrepPack, InterviewPrepPack>
{
    // B3-5 v0: recomputed on read from the advert and the current Story Bank; nothing derived is stored.
    public Task<InterviewPrepPack> HandleAsync(GetImportPrepPack query, CancellationToken ct = default) => reader.ReadAsync(s =>
    {
        Guard.Require(s.ImportedDocuments.TryGetValue(query.Id, out var document), 404, "Import not found.");
        return Jarvis.Orchestrator.Coaching.InterviewPrepPackBuilder.Build(document, s.Stories.Values) with { DataEpoch = PrivacyContext.Epoch };
    }, ct);

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

public sealed record CreateImportPrepPackCards(Guid DocumentId, CreatePrepPackCardsRequest Request) : ICommand<PrepPackCards>;

/// <summary>
/// B3-5: turns selected prep-pack questions into InterviewStory review cards through the existing learning creation
/// path with durable request receipts (C1). Each card's request ID is derived from the advert and the question, so a
/// repeat returns the original card instead of creating a duplicate. Questions come from the fixed, code-owned
/// competency list, never from advert text. Deleting the advert stops new cards; cards already made stay the owner's.
/// </summary>
public sealed class PrepPackCardCommands(IRuntimeStore store, TimeProvider clock) :
    ICommandHandler<CreateImportPrepPackCards, PrepPackCards>
{
    public const string AnswerGuide = "Answer with a real Story Bank story: the situation, what you personally did, the key decision and " +
        "trade-off, and the result. Say \"not measured\" if you have no number; never invent one.";

    public Task<PrepPackCards> HandleAsync(CreateImportPrepPackCards command, CancellationToken ct = default)
    {
        var picked = command.Request.Questions ?? [];
        Guard.Require(picked.Count is > 0 and <= Jarvis.Orchestrator.Coaching.InterviewPrepPackBuilder.MaxQuestions &&
            picked.Distinct().Count() == picked.Count, 400, "Choose between one and twenty distinct questions.");
        Guard.Require(command.Request.DataEpoch is >= 0, 400, "Reload the prep pack to capture its data generation.");
        Guard.Require(PrivacyContext.Epoch == command.Request.DataEpoch, 409, "Prep pack belongs to another data generation. Reload it.");
        Guard.Require(command.Request.ExpectedPackFingerprint is { Length: 64 }, 400, "Reload the prep pack before selecting questions.");
        var epoch = command.Request.DataEpoch.Value;
        return store.UpdateAsync<PrepPackCards>(s => {
            Guard.Require(s.ImportedDocuments.TryGetValue(command.DocumentId, out var document), 404, "Import not found.");
            var pack = Jarvis.Orchestrator.Coaching.InterviewPrepPackBuilder.Build(document, s.Stories.Values);
            Guard.Require(pack.Fingerprint == command.Request.ExpectedPackFingerprint, 409, "Prep pack questions changed. Reload before creating cards.");
            Guard.Require(picked.All(i => i >= 0 && i < pack.LikelyQuestions.Count), 400, "A selected question is not in this prep pack.");
            var cards = new List<LearningItem>(); int created = 0, existing = 0, retired = 0;
            var changes = new List<LearningCreationChange>();
            foreach (var index in picked.Order())
            {
                ct.ThrowIfCancellationRequested();
                var question = pack.LikelyQuestions[index];
                var requestId = Jarvis.Orchestrator.Domains.DomainPolicy.StableId("prep-pack-card:v1:" + pack.DocumentId.ToString("N") + ":" + question);
                try
                {
                    var prior = s.CreationReceipts.GetValueOrDefault(requestId);
                    var change = LearningService.DecideCreate(new(LearningDomain.InterviewStory, question, AnswerGuide, requestId), clock, epoch, prior);
                    cards.Add(change.Result); changes.Add(change);
                    if (prior is not null) existing++; else created++;
                }
                catch (DomainException error) when (error.StatusCode == 410) { retired++; } // aged or source-retired receipt: never recreate
            }
            // Finish every decision before publishing. A later conflict/cancellation cannot leave a partial batch.
            ct.ThrowIfCancellationRequested();
            foreach (var change in changes.Where(c => c.Receipt is not null)) {
                s.Learning.Add(change.Result.Id, change.Result);
                s.CreationReceipts.Add(change.Receipt!.RequestId, change.Receipt);
            }
            return new(created, existing, retired, cards.AsReadOnly());
        }, ct);
    }
}
