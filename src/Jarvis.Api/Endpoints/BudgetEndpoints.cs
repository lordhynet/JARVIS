using Jarvis.Api.Security;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Workflows;

namespace Jarvis.Api.Endpoints;

public static class BudgetEndpoints
{
    public static void MapBudgetEndpoints(this WebApplication app)
    {
        app.MapGet("/api/budget/status", (BudgetApprovalService service, CancellationToken ct) => service.StatusAsync(ct));
        app.MapGet("/api/budget/legacy/{id:guid}/preview", (Guid id, HttpContext context, BudgetManagementAuthorization owner,
            BudgetApprovalService service, CancellationToken ct) => service.PreviewLegacyAsync(owner.Require(context), id, ct));
        app.MapPost("/api/budget/approvals", (HttpContext context, BudgetApprovalRequest request, BudgetManagementAuthorization owner,
            BudgetApprovalService service, CancellationToken ct) => service.ApproveAsync(owner.Require(context), request, ct));
        app.MapPost("/api/budget/approvals/{id:guid}/revoke", (Guid id, HttpContext context, RevokeBudgetApprovalRequest request,
            BudgetManagementAuthorization owner, BudgetApprovalService service, CancellationToken ct) => service.RevokeAsync(owner.Require(context), id, request, ct));
        app.MapPost("/api/budget/legacy/{id:guid}/classify", (Guid id, HttpContext context, ClassifyLegacyChargeRequest request,
            BudgetManagementAuthorization owner, BudgetApprovalService service, CancellationToken ct) => service.ClassifyAsync(owner.Require(context), id, request, ct));
    }
}
