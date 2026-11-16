using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;
using Jarvis.Orchestrator.Cqrs;
using Jarvis.Orchestrator.Cqrs.Agents;

namespace Jarvis.Api.Endpoints;

public static class AgentEndpoints
{
    public static void MapAgentEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization(Jarvis.Api.Security.CorePolicies.Owner);
        api.MapPost("/agents/calendar/sync", (CalendarTriggerSync sync, CancellationToken ct) => sync.SyncAsync(ct));
        api.MapGet("/goals", (IRuntimeReader s, CancellationToken ct) => s.ReadAsync(x => x.Goals.Values.Where(g => g.Status != "Deleted").OrderBy(g => g.NextReviewAt).ToList(), ct));
        api.MapPost("/goals", (SaveGoalRequest request, ICommandHandler<SaveAgentGoal, PersonalGoal> handler, CancellationToken ct) => handler.HandleAsync(new(request), ct));
        api.MapDelete("/goals/{id:guid}", (Guid id, int revision, ICommandHandler<DeleteAgentGoal, bool> handler, CancellationToken ct) => handler.HandleAsync(new(id, revision), ct));
        api.MapGet("/agents/triggers", (IRuntimeReader s, CancellationToken ct) => s.ReadAsync(x => x.AgentTriggers.Values.OrderByDescending(t => t.DueAt).Take(100).ToList(), ct));
        api.MapPost("/agents/triggers", (AgentTriggerRequest request, ICommandHandler<IngestAgentTrigger, DurableAgentTrigger> handler, CancellationToken ct) => handler.HandleAsync(new(request), ct));
        api.MapGet("/usage/charges", (IRuntimeReader s, CancellationToken ct) => s.ReadAsync(x => x.UsageReservations.Values.OrderByDescending(t => t.At).Take(200).ToList(), ct));
        api.MapGet("/usage/reconciliations", (IRuntimeReader s, CancellationToken ct) => s.ReadAsync(x => x.UsageReconciliations.Values.OrderByDescending(t => t.At).Take(200).ToList(), ct));
        api.MapPost("/usage/charges/{id:guid}/reconcile", (Guid id, HttpContext context, ReconcileUsageRequest request,
            Jarvis.Api.Security.BudgetManagementAuthorization owner, UsageReconciler reconciler, CancellationToken ct) => reconciler.ReconcileAsync(owner.Require(context), id, request, ct))
            .RequireAuthorization(Jarvis.Api.Security.CorePolicies.BudgetManage);
        api.MapGet("/agents/catalog", async (Jarvis.Orchestrator.Domains.IWorkflowCatalog catalog, CancellationToken ct) =>
            (await catalog.ListAsync(ct)).Select(x => new { x.Key, x.RequiresCloud, x.UsesPersonalContext, x.Deterministic, x.DomainKey, x.DomainVersion }).ToArray());
        api.MapGet("/agents/controls", (IRuntimeReader s, CancellationToken ct) => s.ReadAsync(x => x.AgentControls, ct));
        api.MapPut("/agents/controls", (AgentControls request, ICommandHandler<UpdateAgentControls, AgentControls> handler, CancellationToken ct) => handler.HandleAsync(new(request), ct));
        api.MapGet("/agents/schedules", (IRuntimeReader s, CancellationToken ct) => s.ReadAsync(x => x.AgentSchedules.Values.OrderBy(y => y.CreatedAt).ToList(), ct));
        api.MapPost("/agents/schedules", (SaveAgentScheduleRequest request, ICommandHandler<SaveAgentSchedule, AgentSchedule> handler, CancellationToken ct) => handler.HandleAsync(new(request), ct));
        api.MapPost("/agents/runs", (StartAgentRunRequest request, ICommandHandler<StartAgentRun, AgentRunView> handler, CancellationToken ct) => handler.HandleAsync(new(request), ct));
        api.MapGet("/agents/runs", (IRuntimeReader s, CancellationToken ct) => s.ReadAsync(x => x.AgentRuns.Values.Where(y => y.DeletedAt is null).OrderByDescending(y => y.CreatedAt).Take(30).Select(y => AgentRuntime.View(x, y)).ToList(), ct));
        api.MapPost("/agents/runs/{id:guid}/cancel", (Guid id, ICommandHandler<CancelAgentRun, bool> handler, CancellationToken ct) => handler.HandleAsync(new(id, false), ct));
        api.MapDelete("/agents/runs/{id:guid}", (Guid id, ICommandHandler<CancelAgentRun, bool> handler, CancellationToken ct) => handler.HandleAsync(new(id, true), ct));
        api.MapGet("/agents/notifications", (IRuntimeReader s, TimeProvider clock, CancellationToken ct) => s.ReadAsync(x => x.AgentOutbox.Values.Where(y => y.Delivered && y.ExpiresAt > clock.GetUtcNow()).OrderByDescending(y => y.CreatedAt).Take(30).ToList(), ct));
        api.MapPost("/agents/notifications/{id:guid}/read", (Guid id, ICommandHandler<MarkAgentNotificationRead, bool> handler, CancellationToken ct) => handler.HandleAsync(new(id), ct));
    }
}
