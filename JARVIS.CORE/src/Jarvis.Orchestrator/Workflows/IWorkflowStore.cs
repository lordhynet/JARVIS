using Jarvis.Contracts;

namespace Jarvis.Orchestrator.Workflows;

public interface IWorkflowReader
{
    Task<WorkflowRun?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<WorkflowRun>> ListAsync(CancellationToken ct = default);
    Task<IReadOnlyList<WorkflowEvent>> EventsAsync(Guid id, int afterRevision, CancellationToken ct = default);
}

public interface IWorkflowStore : IWorkflowReader
{
    Task<WorkflowRun> AddAsync(WorkflowRun candidate, CancellationToken ct = default);
    // Claims and mutations commit the run and its event atomically. No provider I/O in callbacks.
    Task<WorkflowRun?> ClaimAsync(DateTimeOffset now, Func<WorkflowRun, WorkflowRun> claim, CancellationToken ct = default);
    Task<WorkflowRun?> UpdateAsync(Guid id, Func<WorkflowRun, WorkflowRun> update, CancellationToken ct = default);
}
