using Jarvis.Contracts;
using Jarvis.Orchestrator.Cqrs;
using Jarvis.Orchestrator.Cqrs.Tools;

namespace Jarvis.Api.Endpoints;

public static class ToolEndpoints
{
    public static void MapToolEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/tools").WithTags("Tools");
        group.MapGet("/", (IQueryHandler<ListToolActions, IReadOnlyList<AuditedTool>> handler, CancellationToken ct) =>
            handler.HandleAsync(new ListToolActions(), ct));
        group.MapPost("/home", (HomeActionRequest request, ICommandHandler<RequestHomeAction, AuditedTool> handler, CancellationToken ct) =>
            handler.HandleAsync(new RequestHomeAction(request), ct));
        group.MapPost("/{id:guid}/confirm", (Guid id, ConfirmRequest request, ICommandHandler<ConfirmToolAction, AuditedTool> handler, CancellationToken ct) =>
            handler.HandleAsync(new ConfirmToolAction(id, request.Approve), ct));
    }
    public sealed record ConfirmRequest(bool Approve);
}
