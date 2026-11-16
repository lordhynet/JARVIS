using Jarvis.Api.Security;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Workflows;
using Jarvis.Orchestrator.Cqrs;
using Jarvis.Orchestrator.Cqrs.Budget;

namespace Jarvis.Api.Endpoints;

public static class BudgetEndpoints
{
    public static void MapBudgetEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization(CorePolicies.Owner);
        api.MapGet("/budget/status", (IQueryHandler<GetBudgetStatus, GbpBudgetStatus> handler, CancellationToken ct) => handler.HandleAsync(new(), ct));
        api.MapGet("/budget/legacy/{id:guid}/preview", (Guid id, HttpContext context, BudgetManagementAuthorization owner,
            IQueryHandler<PreviewLegacyBudgetCharge, LegacyChargePreview> handler, CancellationToken ct) =>
            handler.HandleAsync(new(id, owner.Require(context)), ct)).RequireAuthorization(CorePolicies.BudgetManage);
        api.MapPost("/budget/approvals", (HttpContext context, BudgetApprovalRequest request, BudgetManagementAuthorization owner,
            ICommandHandler<ApproveMonthlyBudget, BudgetApproval> handler, CancellationToken ct) =>
            handler.HandleAsync(new(request, owner.Require(context)), ct)).RequireAuthorization(CorePolicies.BudgetManage);
        api.MapPost("/budget/approvals/{id:guid}/revoke", (Guid id, HttpContext context, RevokeBudgetApprovalRequest request,
            BudgetManagementAuthorization owner, ICommandHandler<RevokeMonthlyBudgetApproval, BudgetApproval> handler, CancellationToken ct) =>
            handler.HandleAsync(new(id, request, owner.Require(context)), ct)).RequireAuthorization(CorePolicies.BudgetManage);
        api.MapPost("/budget/legacy/{id:guid}/classify", (Guid id, HttpContext context, ClassifyLegacyChargeRequest request,
            BudgetManagementAuthorization owner, ICommandHandler<ClassifyLegacyBudgetCharge, GbpCommitment> handler, CancellationToken ct) =>
            handler.HandleAsync(new(id, request, owner.Require(context)), ct)).RequireAuthorization(CorePolicies.BudgetManage);
    }
}
