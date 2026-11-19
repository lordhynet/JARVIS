using Jarvis.Orchestrator.Intelligence;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Cqrs.Agents;

public sealed record RunIntelligenceEvaluation(RunIntelligenceEvaluationRequest Request, BudgetActor Actor) : ICommand<IntelligenceEvaluationReport>;
public sealed class IntelligenceEvaluationCommands(IIntelligenceEvaluator evaluator) : ICommandHandler<RunIntelligenceEvaluation, IntelligenceEvaluationReport>
{
    public Task<IntelligenceEvaluationReport> HandleAsync(RunIntelligenceEvaluation command, CancellationToken ct = default) => evaluator.RunAsync(command.Request, command.Actor, ct);
}
