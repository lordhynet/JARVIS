using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;

namespace Jarvis.Orchestrator.Cqrs.Budget;

public sealed record GetBudgetStatus : IQuery<GbpBudgetStatus>;
public sealed record PreviewLegacyBudgetCharge(Guid Id, BudgetActor Actor) : IQuery<LegacyChargePreview>;
public sealed record ApproveMonthlyBudget(BudgetApprovalRequest Request, BudgetActor Actor) : ICommand<BudgetApproval>;
public sealed record RevokeMonthlyBudgetApproval(Guid Id, RevokeBudgetApprovalRequest Request, BudgetActor Actor) : ICommand<BudgetApproval>;
public sealed record ClassifyLegacyBudgetCharge(Guid Id, ClassifyLegacyChargeRequest Request, BudgetActor Actor) : ICommand<GbpCommitment>;

public sealed class BudgetQueryHandlers(BudgetApprovalService service) :
    IQueryHandler<GetBudgetStatus, GbpBudgetStatus>, IQueryHandler<PreviewLegacyBudgetCharge, LegacyChargePreview>
{
    public Task<GbpBudgetStatus> HandleAsync(GetBudgetStatus query, CancellationToken ct = default) => service.StatusAsync(ct);
    public Task<LegacyChargePreview> HandleAsync(PreviewLegacyBudgetCharge query, CancellationToken ct = default) =>
        service.PreviewLegacyAsync(query.Actor, query.Id, ct);
}

public sealed class BudgetCommandHandlers(BudgetApprovalService service) :
    ICommandHandler<ApproveMonthlyBudget, BudgetApproval>, ICommandHandler<RevokeMonthlyBudgetApproval, BudgetApproval>,
    ICommandHandler<ClassifyLegacyBudgetCharge, GbpCommitment>
{
    public Task<BudgetApproval> HandleAsync(ApproveMonthlyBudget command, CancellationToken ct = default) =>
        service.ApproveAsync(command.Actor, command.Request, ct);
    public Task<BudgetApproval> HandleAsync(RevokeMonthlyBudgetApproval command, CancellationToken ct = default) =>
        service.RevokeAsync(command.Actor, command.Id, command.Request, ct);
    public Task<GbpCommitment> HandleAsync(ClassifyLegacyBudgetCharge command, CancellationToken ct = default) =>
        service.ClassifyAsync(command.Actor, command.Id, command.Request, ct);
}
