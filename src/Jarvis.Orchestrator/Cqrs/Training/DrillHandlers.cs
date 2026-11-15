using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Training;

namespace Jarvis.Orchestrator.Cqrs.Training;

public sealed record StartTrainingDrill(StartTrainingDrillRequest Request) : ICommand<TrainingDrillView>;
public sealed record CancelTrainingDrill(Guid Id, int Revision) : ICommand<TrainingDrillView>;
public sealed record GetTrainingDrill(Guid Id) : IQuery<TrainingDrillView>;
public sealed record ListTrainingDrills(Guid SessionId) : IQuery<IReadOnlyList<TrainingDrillView>>;
public sealed class DrillCommandHandlers(TrainingDrillService service) : ICommandHandler<StartTrainingDrill, TrainingDrillView>, ICommandHandler<CancelTrainingDrill, TrainingDrillView>
{
    public Task<TrainingDrillView> HandleAsync(StartTrainingDrill c, CancellationToken ct = default) => service.StartAsync(c.Request, ct);
    public Task<TrainingDrillView> HandleAsync(CancelTrainingDrill c, CancellationToken ct = default) => service.CancelAsync(c.Id, c.Revision, ct);
}
public sealed class DrillQueryHandlers(IRuntimeReader reader) : IQueryHandler<GetTrainingDrill, TrainingDrillView>, IQueryHandler<ListTrainingDrills, IReadOnlyList<TrainingDrillView>>
{
    public Task<TrainingDrillView> HandleAsync(GetTrainingDrill q, CancellationToken ct = default) => reader.ReadAsync(s =>
    {
        Guard.Require(s.TrainingDrills.TryGetValue(q.Id, out var drill), 404, "Guided drill not found."); return TrainingDrillService.View(s, drill);
    }, ct);
    public Task<IReadOnlyList<TrainingDrillView>> HandleAsync(ListTrainingDrills q, CancellationToken ct = default) => reader.ReadAsync<IReadOnlyList<TrainingDrillView>>(s =>
        s.TrainingDrills.Values.Where(d => d.SessionId == q.SessionId).OrderByDescending(d => d.CreatedAt).Take(30)
            .Select(d => new TrainingDrillView(d, [])).ToList(), ct);
}
