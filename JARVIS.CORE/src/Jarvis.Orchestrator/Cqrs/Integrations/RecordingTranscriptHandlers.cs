using Jarvis.Contracts;
using Jarvis.Orchestrator.Recordings;

namespace Jarvis.Orchestrator.Cqrs.Integrations;

public sealed record GetRecordingTranscript(Guid Id, bool? Locked) : IQuery<RecordingTranscriptProgress?>;
public sealed record QuoteRecordingTranscript(Guid Id, QuoteRecordingTranscriptRequest Request) : ICommand<RecordingTranscriptProgress>;
public sealed record AdvanceRecordingTranscript(Guid Id, AdvanceRecordingTranscriptRequest Request) : ICommand<RecordingTranscriptProgress>;
public sealed record RelabelRecordingTranscript(Guid Id, RelabelRecordingSegmentRequest Request) : ICommand<ImportedDocument>;
public sealed class RecordingTranscriptQueries(RecordingTranscriptService service) : IQueryHandler<GetRecordingTranscript, RecordingTranscriptProgress?>
{
    public Task<RecordingTranscriptProgress?> HandleAsync(GetRecordingTranscript query, CancellationToken ct = default) => service.GetAsync(query.Id, query.Locked, ct);
}
public sealed class RecordingTranscriptCommands(RecordingTranscriptService service) : ICommandHandler<QuoteRecordingTranscript, RecordingTranscriptProgress>,
    ICommandHandler<AdvanceRecordingTranscript, RecordingTranscriptProgress>, ICommandHandler<RelabelRecordingTranscript, ImportedDocument>
{
    public Task<RecordingTranscriptProgress> HandleAsync(QuoteRecordingTranscript command, CancellationToken ct = default) => service.QuoteAsync(command.Id, command.Request, ct);
    public Task<RecordingTranscriptProgress> HandleAsync(AdvanceRecordingTranscript command, CancellationToken ct = default) => service.AdvanceAsync(command.Id, command.Request, ct);
    public Task<ImportedDocument> HandleAsync(RelabelRecordingTranscript command, CancellationToken ct = default) => service.RelabelAsync(command.Id, command.Request, ct);
}
