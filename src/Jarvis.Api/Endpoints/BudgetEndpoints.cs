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
        app.MapGet("/api/budget/status", (IQueryHandler<GetBudgetStatus, GbpBudgetStatus> handler, CancellationToken ct) => handler.HandleAsync(new(), ct));
        app.MapGet("/api/budget/legacy/{id:guid}/preview", (Guid id, HttpContext context, BudgetManagementAuthorization owner,
            IQueryHandler<PreviewLegacyBudgetCharge, LegacyChargePreview> handler, CancellationToken ct) =>
            handler.HandleAsync(new(id, owner.Require(context)), ct));
        app.MapPost("/api/budget/approvals", (HttpContext context, BudgetApprovalRequest request, BudgetManagementAuthorization owner,
            ICommandHandler<ApproveMonthlyBudget, BudgetApproval> handler, CancellationToken ct) =>
            handler.HandleAsync(new(request, owner.Require(context)), ct));
        app.MapPost("/api/budget/approvals/{id:guid}/revoke", (Guid id, HttpContext context, RevokeBudgetApprovalRequest request,
            BudgetManagementAuthorization owner, ICommandHandler<RevokeMonthlyBudgetApproval, BudgetApproval> handler, CancellationToken ct) =>
            handler.HandleAsync(new(id, request, owner.Require(context)), ct));
        app.MapPost("/api/budget/legacy/{id:guid}/classify", (Guid id, HttpContext context, ClassifyLegacyChargeRequest request,
            BudgetManagementAuthorization owner, ICommandHandler<ClassifyLegacyBudgetCharge, GbpCommitment> handler, CancellationToken ct) =>
            handler.HandleAsync(new(id, request, owner.Require(context)), ct));
    }
}
