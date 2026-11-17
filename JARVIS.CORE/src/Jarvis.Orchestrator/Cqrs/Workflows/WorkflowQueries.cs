using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;

namespace Jarvis.Orchestrator.Cqrs.Workflows;

public sealed record ListWorkflows : IQuery<IReadOnlyList<WorkflowRun>>;
public sealed record GetWorkflow(Guid Id) : IQuery<WorkflowRun>;
public sealed record GetWorkflowEvents(Guid Id, int AfterRevision = 0) : IQuery<IReadOnlyList<WorkflowEvent>>;

public sealed class WorkflowQueryHandlers(IWorkflowReader reader, IRuntimeReader runtime) :
    IQueryHandler<ListWorkflows, IReadOnlyList<WorkflowRun>>, IQueryHandler<GetWorkflow, WorkflowRun>,
    IQueryHandler<GetWorkflowEvents, IReadOnlyList<WorkflowEvent>>
{
    public async Task<IReadOnlyList<WorkflowRun>> HandleAsync(ListWorkflows query, CancellationToken ct = default)
    {
        var runs = await reader.ListAsync(ct);
        return await runtime.ReadAsync<IReadOnlyList<WorkflowRun>>(s => runs.Select(r => Protect(r, s)).ToList(), ct);
    }
    public async Task<WorkflowRun> HandleAsync(GetWorkflow query, CancellationToken ct = default)
    {
        var run = await reader.GetAsync(query.Id, ct);
        if (run is null || run.DeletedAt is not null) throw new DomainException(404, "Workflow not found.");
        return await runtime.ReadAsync(s => Protect(run, s), ct);
    }
    public async Task<IReadOnlyList<WorkflowEvent>> HandleAsync(GetWorkflowEvents query, CancellationToken ct = default)
    {
        Guard.Require(query.AfterRevision >= 0, 400, "AfterRevision cannot be negative.");
        var run = await reader.GetAsync(query.Id, ct);
        if (run is null || run.DeletedAt is not null) throw new DomainException(404, "Workflow not found.");
        return await reader.EventsAsync(query.Id, query.AfterRevision, ct);
    }
    private static WorkflowRun Protect(WorkflowRun run, RuntimeState state) =>
        run.Brief is not null && run.Brief.SourceIds.Any(id => !state.Stories.ContainsKey(id) && !state.Learning.ContainsKey(id))
            ? run with { Brief = null, ErrorCode = "source_unavailable" } : run;
}
