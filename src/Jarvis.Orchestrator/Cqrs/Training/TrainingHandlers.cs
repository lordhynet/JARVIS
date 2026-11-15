using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Training;

namespace Jarvis.Orchestrator.Cqrs.Training;

public sealed record SubmitTraining(SubmitTrainingAnswerRequest Request) : ICommand<TrainingAttempt>;
public sealed record AssessTraining(Guid Id, SelfAssessmentRequest Request) : ICommand<TrainingAttempt>;
public sealed record CorrectTraining(Guid Id, CorrectTrainingAnswerRequest Request) : ICommand<TrainingAttempt>;
public sealed record DeleteTraining(Guid Id, int Revision) : ICommand<bool>;
public sealed class TrainingCommandHandlers(TrainingService service) :
    ICommandHandler<SubmitTraining, TrainingAttempt>, ICommandHandler<AssessTraining, TrainingAttempt>,
    ICommandHandler<CorrectTraining, TrainingAttempt>, ICommandHandler<DeleteTraining, bool>
{
    public Task<TrainingAttempt> HandleAsync(SubmitTraining c, CancellationToken ct = default) => service.SubmitAsync(c.Request, ct);
    public Task<TrainingAttempt> HandleAsync(AssessTraining c, CancellationToken ct = default) => service.AssessAsync(c.Id, c.Request, ct);
    public Task<TrainingAttempt> HandleAsync(CorrectTraining c, CancellationToken ct = default) => service.CorrectAsync(c.Id, c.Request, ct);
    public Task<bool> HandleAsync(DeleteTraining c, CancellationToken ct = default) => service.DeleteAsync(c.Id, c.Revision, ct);
}

public sealed record GetTraining(Guid Id) : IQuery<TrainingAttempt>;
public sealed record ListTraining(Guid SessionId) : IQuery<IReadOnlyList<TrainingAttemptSummary>>;
public sealed record GetTrainingDashboard(string TimeZone) : IQuery<TrainingDashboard>;
public sealed record ListTrainingCards : IQuery<IReadOnlyList<LearningItem>>;
public sealed class TrainingQueryHandlers(IRuntimeReader reader, TimeProvider clock) :
    IQueryHandler<GetTraining, TrainingAttempt>, IQueryHandler<ListTraining, IReadOnlyList<TrainingAttemptSummary>>,
    IQueryHandler<GetTrainingDashboard, TrainingDashboard>, IQueryHandler<ListTrainingCards, IReadOnlyList<LearningItem>>
{
    public Task<IReadOnlyList<LearningItem>> HandleAsync(ListTrainingCards q, CancellationToken ct = default) =>
        reader.ReadAsync<IReadOnlyList<LearningItem>>(s => QuestionBank.All.Where(x => s.Learning.ContainsKey(x.Id)).Select(x => s.Learning[x.Id]).ToList(), ct);
    public Task<TrainingAttempt> HandleAsync(GetTraining q, CancellationToken ct = default) => reader.ReadAsync(s =>
    {
        Guard.Require(s.TrainingAttempts.TryGetValue(q.Id, out var attempt) && attempt.DeletedAt is null, 404, "Training attempt not found.");
        return attempt;
    }, ct);
    public Task<IReadOnlyList<TrainingAttemptSummary>> HandleAsync(ListTraining q, CancellationToken ct = default) =>
        reader.ReadAsync<IReadOnlyList<TrainingAttemptSummary>>(s => s.TrainingAttempts.Values
            .Where(x => x.DeletedAt is null && x.SessionId == q.SessionId).OrderByDescending(x => x.Sequence)
            .Take(50).Select(TrainingProjection.Summary).ToList(), ct);
    public Task<TrainingDashboard> HandleAsync(GetTrainingDashboard q, CancellationToken ct = default)
    {
        var zone = TrainingProjection.Zone(q.TimeZone);
        return reader.ReadAsync(s =>
        {
            var attempts = s.TrainingAttempts.Values.Where(x => x.DeletedAt is null).ToList();
            var ids = QuestionBank.All.Select(x => x.Id).ToHashSet();
            var cards = s.Learning.Values.Where(x => ids.Contains(x.Id)).ToList();
            return new TrainingDashboard(attempts.Count(x => !x.Skipped), attempts.Count(x => x.Skipped),
                attempts.Where(x => !x.Skipped).Select(x => TimeZoneInfo.ConvertTime(x.SubmittedAt, zone).Date).Distinct().Count(),
                cards.Count(x => x.DueAt <= clock.GetUtcNow()),
                cards.Where(x => x.MasteryScore.HasValue).GroupBy(x => x.Domain)
                    .Select(g => new WeakDomain(g.Key, g.Average(x => x.MasteryScore!.Value), g.Count(), string.Join(", ", g.Select(x => x.MasterySource).Distinct()))).OrderBy(x => x.MeanScore).ToList(),
                attempts.OrderByDescending(x => x.Sequence).Take(10).Select(TrainingProjection.Summary).ToList(), zone.Id);
        }, ct);
    }
}
