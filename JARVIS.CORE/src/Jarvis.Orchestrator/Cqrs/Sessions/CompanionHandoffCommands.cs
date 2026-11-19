using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
namespace Jarvis.Orchestrator.Cqrs.Sessions;
public sealed record ControlCompanionHandoff(CompanionHandoffRequest Request) : ICommand<CompanionHandoffView>;
public sealed class CompanionHandoffCommands(CompanionHandoffService service) : ICommandHandler<ControlCompanionHandoff, CompanionHandoffView>
{
    public Task<CompanionHandoffView> HandleAsync(ControlCompanionHandoff command, CancellationToken ct = default) => service.ControlAsync(command.Request, ct);
}
