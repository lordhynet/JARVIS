using Jarvis.Api.Security;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Cqrs;
using Jarvis.Orchestrator.Cqrs.Tools;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Api.Endpoints;

public static class ToolEndpoints
{
    public static void MapToolEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tools").WithTags("Tools").RequireAuthorization(Jarvis.Api.Security.CorePolicies.Owner);
        group.MapGet("/", (IQueryHandler<ListToolActions, IReadOnlyList<AuditedTool>> handler, CancellationToken ct) =>
            handler.HandleAsync(new ListToolActions(), ct));
        group.MapPost("/home", (HomeActionRequest request, ICommandHandler<RequestHomeAction, AuditedTool> handler, CancellationToken ct) =>
            handler.HandleAsync(new RequestHomeAction(request), ct));
        group.MapPost("/{id:guid}/confirm", (Guid id, ConfirmRequest request, HttpContext context, OwnerAuthorization owner,
            ICommandHandler<ConfirmToolAction, AuditedTool> handler, CancellationToken ct) =>
            handler.HandleAsync(new ConfirmToolAction(id, request.Approve, owner.Require(context, ToolService.ConfirmScope)), ct))
            .RequireAuthorization(CorePolicies.ToolConfirm);
    }
    public sealed record ConfirmRequest(bool Approve);
}
