using Jarvis.Contracts;
using Jarvis.Orchestrator.Recordings;

namespace Jarvis.Orchestrator.Cqrs.Integrations;

public sealed record ListRecordings(bool? Locked) : IQuery<IReadOnlyList<InterviewRecording>>;
public sealed record PrepareRecording(PrepareRecordingRequest Request) : ICommand<InterviewRecording>;
public sealed record CaptureRecording(RecordingCaptureRequest Request) : ICommand<RecordingCaptureResult>;
public sealed record DeleteRecording(Guid Id, int Revision, bool? Locked) : ICommand<bool>;
public sealed class RecordingQueries(RecordingService recordings) : IQueryHandler<ListRecordings, IReadOnlyList<InterviewRecording>>
{
    public Task<IReadOnlyList<InterviewRecording>> HandleAsync(ListRecordings query, CancellationToken ct = default) {
        RecordingService.RequireManualUnlocked(query.Locked); return recordings.ListAsync(ct);
    }
}
public sealed class RecordingCommands(RecordingService recordings) : ICommandHandler<PrepareRecording, InterviewRecording>,
    ICommandHandler<CaptureRecording, RecordingCaptureResult>, ICommandHandler<DeleteRecording, bool>
{
    public Task<InterviewRecording> HandleAsync(PrepareRecording command, CancellationToken ct = default) => recordings.PrepareAsync(command.Request, ct);
    public Task<RecordingCaptureResult> HandleAsync(CaptureRecording command, CancellationToken ct = default) => recordings.CaptureAsync(command.Request, ct);
    public Task<bool> HandleAsync(DeleteRecording command, CancellationToken ct = default) {
        RecordingService.RequireManualUnlocked(command.Locked); return recordings.DeleteAsync(command.Id, command.Revision, ct);
    }
}
