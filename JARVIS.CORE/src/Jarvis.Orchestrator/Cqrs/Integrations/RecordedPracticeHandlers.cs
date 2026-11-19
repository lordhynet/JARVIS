using Jarvis.Contracts;
using Jarvis.Orchestrator.Recordings;
namespace Jarvis.Orchestrator.Cqrs.Integrations;

public sealed record GetRecordedPractice(Guid Id, bool? Locked) : IQuery<RecordedPracticePage>;
public sealed record GetRecordedClip(Guid Id, Guid Question, Guid Piece, int Revision, bool? Locked) : IQuery<byte[]>;
public sealed record BeginRecordedPractice(Guid Id, StartRecordedPractice Request) : ICommand<RecordedPracticeResult>;
public sealed record ApproveRecordedPracticeCard(Guid Id, ApproveRecordedCard Request) : ICommand<LearningItem>;
public sealed class RecordedPracticeQueries(RecordedInterviewPractice service) : IQueryHandler<GetRecordedPractice, RecordedPracticePage>, IQueryHandler<GetRecordedClip, byte[]>
{
    public Task<RecordedPracticePage> HandleAsync(GetRecordedPractice q, CancellationToken ct = default) => service.GetAsync(q.Id, q.Locked, ct);
    public Task<byte[]> HandleAsync(GetRecordedClip q, CancellationToken ct = default) => service.ClipAsync(q.Id, q.Question, q.Piece, q.Revision, q.Locked, ct);
}
public sealed class RecordedPracticeCommands(RecordedInterviewPractice service) : ICommandHandler<BeginRecordedPractice, RecordedPracticeResult>, ICommandHandler<ApproveRecordedPracticeCard, LearningItem>
{
    public Task<RecordedPracticeResult> HandleAsync(BeginRecordedPractice q, CancellationToken ct = default) => service.StartAsync(q.Id, q.Request, ct);
    public Task<LearningItem> HandleAsync(ApproveRecordedPracticeCard q, CancellationToken ct = default) => service.ApproveCardAsync(q.Id, q.Request, ct);
}
