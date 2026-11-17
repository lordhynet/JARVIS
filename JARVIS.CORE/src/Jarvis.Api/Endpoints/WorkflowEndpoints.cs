using Jarvis.Contracts;
using Jarvis.Orchestrator.Cqrs;
using Jarvis.Orchestrator.Cqrs.Workflows;
using Jarvis.Api.Integrations;

namespace Jarvis.Api.Endpoints;

public static class WorkflowEndpoints
{
    public static void MapWorkflowEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/workflows").WithTags("Workflows").RequireAuthorization(Jarvis.Api.Security.CorePolicies.Owner);
        group.MapGet("/paid-ai", (PaidAiProvider provider, CancellationToken ct) => provider.StatusAsync(ct));
        group.MapPost("/interview-briefs", async (CreateInterviewBriefRequest request,
            PaidAiProvider provider, ICommandHandler<CreateInterviewBrief, WorkflowRun> handler, CancellationToken ct) =>
        {
            var status = await provider.StatusAsync(ct);
            Jarvis.Orchestrator.Runtime.Guard.Require(status.Ready, 503, status.Detail);
            var run = await handler.HandleAsync(new CreateInterviewBrief(request), ct);
            return Results.Accepted($"/api/workflows/{run.Id}", WorkflowRunView.From(run));
        }).RequireRateLimiting("cloud");
        group.MapPost("/practice-plans", async (CreatePracticePlanRequest request,
            ICommandHandler<CreatePracticePlan, WorkflowRun> handler, CancellationToken ct) =>
        {
            var run = await handler.HandleAsync(new CreatePracticePlan(request), ct);
            return Results.Accepted($"/api/workflows/{run.Id}", WorkflowRunView.From(run));
        });
        group.MapGet("/", async (IQueryHandler<ListWorkflows, IReadOnlyList<WorkflowRun>> handler, CancellationToken ct) =>
            (await handler.HandleAsync(new ListWorkflows(), ct)).Select(WorkflowRunView.From));
        group.MapGet("/{id:guid}", async (Guid id, IQueryHandler<GetWorkflow, WorkflowRun> handler, CancellationToken ct) =>
            WorkflowRunView.From(await handler.HandleAsync(new GetWorkflow(id), ct)));
        group.MapDelete("/{id:guid}", async (Guid id, ICommandHandler<DeleteWorkflow, CommandCompleted> handler, CancellationToken ct) =>
        { await handler.HandleAsync(new DeleteWorkflow(id), ct); return Results.NoContent(); });
        group.MapPost("/{id:guid}/cancel", async (Guid id, ICommandHandler<CancelWorkflow, WorkflowRun> handler, CancellationToken ct) =>
            WorkflowRunView.From(await handler.HandleAsync(new CancelWorkflow(id), ct)));
        group.MapGet("/{id:guid}/events", (Guid id, int? afterRevision,
            IQueryHandler<GetWorkflowEvents, IReadOnlyList<WorkflowEvent>> handler, CancellationToken ct) =>
            handler.HandleAsync(new GetWorkflowEvents(id, afterRevision ?? 0), ct));
    }
}
