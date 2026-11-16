using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;

namespace Jarvis.Orchestrator.Cqrs.Agents;

public sealed record SaveAgentGoal(SaveGoalRequest Request) : ICommand<PersonalGoal>;
public sealed record DeleteAgentGoal(Guid Id, int Revision) : ICommand<bool>;
public sealed record IngestAgentTrigger(AgentTriggerRequest Request) : ICommand<DurableAgentTrigger>;
public sealed record UpdateAgentControls(AgentControls Controls) : ICommand<AgentControls>;
public sealed record SaveAgentSchedule(SaveAgentScheduleRequest Request) : ICommand<AgentSchedule>;
public sealed record StartAgentRun(StartAgentRunRequest Request) : ICommand<AgentRunView>;
public sealed record CancelAgentRun(Guid Id, bool Delete) : ICommand<bool>;
public sealed record MarkAgentNotificationRead(Guid Id) : ICommand<bool>;

public sealed class AgentCommandHandlers(AgentRuntime agents, IRuntimeStore store) :
    ICommandHandler<SaveAgentGoal, PersonalGoal>, ICommandHandler<DeleteAgentGoal, bool>,
    ICommandHandler<IngestAgentTrigger, DurableAgentTrigger>, ICommandHandler<UpdateAgentControls, AgentControls>,
    ICommandHandler<SaveAgentSchedule, AgentSchedule>, ICommandHandler<StartAgentRun, AgentRunView>,
    ICommandHandler<CancelAgentRun, bool>, ICommandHandler<MarkAgentNotificationRead, bool>
{
    public Task<PersonalGoal> HandleAsync(SaveAgentGoal command, CancellationToken ct = default) => agents.SaveGoalAsync(command.Request, ct);
    public Task<bool> HandleAsync(DeleteAgentGoal command, CancellationToken ct = default) => agents.DeleteGoalAsync(command.Id, command.Revision, ct);
    public Task<DurableAgentTrigger> HandleAsync(IngestAgentTrigger command, CancellationToken ct = default) => agents.IngestTriggerAsync(command.Request, ct);
    public Task<AgentControls> HandleAsync(UpdateAgentControls command, CancellationToken ct = default) => agents.ControlsAsync(command.Controls, ct);
    public Task<AgentSchedule> HandleAsync(SaveAgentSchedule command, CancellationToken ct = default) => agents.SaveScheduleAsync(command.Request, ct);
    public Task<AgentRunView> HandleAsync(StartAgentRun command, CancellationToken ct = default) => agents.StartAsync(command.Request, ct);
    public Task<bool> HandleAsync(CancelAgentRun command, CancellationToken ct = default) => agents.CancelAsync(command.Id, command.Delete, ct);
    public Task<bool> HandleAsync(MarkAgentNotificationRead command, CancellationToken ct = default) => store.UpdateAsync(state =>
    {
        Guard.Require(state.AgentOutbox.TryGetValue(command.Id, out var notice), 404, "Notification not found.");
        state.AgentOutbox[command.Id] = notice with { Read = true };
        return true;
    }, ct);
}
