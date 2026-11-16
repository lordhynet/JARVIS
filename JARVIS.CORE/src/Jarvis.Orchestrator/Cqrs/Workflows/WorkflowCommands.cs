using Jarvis.Contracts;
using Jarvis.Orchestrator.Workflows;

namespace Jarvis.Orchestrator.Cqrs.Workflows;

public sealed record CreatePracticePlan(CreatePracticePlanRequest Request) : ICommand<WorkflowRun>;
public sealed record CancelWorkflow(Guid Id) : ICommand<WorkflowRun>;
public sealed record CreateInterviewBrief(CreateInterviewBriefRequest Request) : ICommand<WorkflowRun>;
public sealed record DeleteWorkflow(Guid Id) : ICommand<CommandCompleted>;

public sealed class WorkflowCommandHandlers(WorkflowService service) :
    ICommandHandler<CreatePracticePlan, WorkflowRun>, ICommandHandler<CancelWorkflow, WorkflowRun>,
    ICommandHandler<CreateInterviewBrief, WorkflowRun>, ICommandHandler<DeleteWorkflow, CommandCompleted>
{
    public Task<WorkflowRun> HandleAsync(CreatePracticePlan command, CancellationToken ct = default) => service.CreateAsync(command.Request, ct);
    public Task<WorkflowRun> HandleAsync(CancelWorkflow command, CancellationToken ct = default) => service.CancelAsync(command.Id, ct);
    public Task<WorkflowRun> HandleAsync(CreateInterviewBrief command, CancellationToken ct = default) => service.CreateBriefAsync(command.Request, ct);
    public async Task<CommandCompleted> HandleAsync(DeleteWorkflow command, CancellationToken ct = default)
    { await service.DeleteAsync(command.Id, ct); return new(); }
}
