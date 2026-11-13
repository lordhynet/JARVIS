using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Cqrs.Tools;

public sealed record RequestHomeAction(HomeActionRequest Request) : ICommand<AuditedTool>;
public sealed record ConfirmToolAction(Guid ToolId, bool Approve) : ICommand<AuditedTool>;
public sealed record RecoverInterruptedTools : ICommand<int>;

public sealed class ToolCommandHandlers(ToolService tools) :
    ICommandHandler<RequestHomeAction, AuditedTool>, ICommandHandler<ConfirmToolAction, AuditedTool>, ICommandHandler<RecoverInterruptedTools, int>
{
    public Task<AuditedTool> HandleAsync(RequestHomeAction command, CancellationToken ct = default) => tools.RequestAsync(command.Request, ct);
    public Task<AuditedTool> HandleAsync(ConfirmToolAction command, CancellationToken ct = default) => tools.ConfirmAsync(command.ToolId, command.Approve, ct);
    public Task<int> HandleAsync(RecoverInterruptedTools command, CancellationToken ct = default) => tools.RecoverInterruptedAsync(ct);
}
