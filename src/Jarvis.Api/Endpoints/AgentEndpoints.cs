using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;

namespace Jarvis.Api.Endpoints;

public static class AgentEndpoints
{
    public static void MapAgentEndpoints(this WebApplication app)
    {
        app.MapPost("/api/agents/calendar/sync", (CalendarTriggerSync sync, CancellationToken ct) => sync.SyncAsync(ct));
        app.MapGet("/api/goals", (IRuntimeReader s, CancellationToken ct) => s.ReadAsync(x => x.Goals.Values.Where(g => g.Status != "Deleted").OrderBy(g => g.NextReviewAt).ToList(), ct));
        app.MapPost("/api/goals", (SaveGoalRequest request, AgentRuntime runtime, CancellationToken ct) => runtime.SaveGoalAsync(request, ct));
        app.MapDelete("/api/goals/{id:guid}", (Guid id, int revision, AgentRuntime runtime, CancellationToken ct) => runtime.DeleteGoalAsync(id, revision, ct));
        app.MapGet("/api/agents/triggers", (IRuntimeReader s, CancellationToken ct) => s.ReadAsync(x => x.AgentTriggers.Values.OrderByDescending(t => t.DueAt).Take(100).ToList(), ct));
        app.MapPost("/api/agents/triggers", (AgentTriggerRequest request, AgentRuntime runtime, CancellationToken ct) => runtime.IngestTriggerAsync(request, ct));
        app.MapGet("/api/usage/charges", (IRuntimeReader s, CancellationToken ct) => s.ReadAsync(x => x.UsageReservations.Values.OrderByDescending(t => t.At).Take(200).ToList(), ct));
        app.MapGet("/api/usage/reconciliations", (IRuntimeReader s, CancellationToken ct) => s.ReadAsync(x => x.UsageReconciliations.Values.OrderByDescending(t => t.At).Take(200).ToList(), ct));
        app.MapPost("/api/usage/charges/{id:guid}/reconcile", (Guid id, HttpContext context, ReconcileUsageRequest request,
            Jarvis.Api.Security.BudgetManagementAuthorization owner, UsageReconciler reconciler, CancellationToken ct) => reconciler.ReconcileAsync(owner.Require(context), id, request, ct));
        app.MapGet("/api/agents/catalog", (Jarvis.Orchestrator.Domains.IWorkflowCatalog catalog) =>
            Jarvis.Orchestrator.Domains.WorkflowCatalog.BuiltIns.Select(x => x.Key).Intersect(catalog.Keys)
                .Concat(catalog.Keys.Except(Jarvis.Orchestrator.Domains.WorkflowCatalog.BuiltIns.Select(x => x.Key)))
                .Select(key => catalog.Get(key)).Select(x => new { x.Key, x.RequiresCloud, x.UsesPersonalContext, x.Deterministic }).ToArray());
        app.MapGet("/api/agents/controls", (IRuntimeReader s, CancellationToken ct) => s.ReadAsync(x => x.AgentControls, ct));
        app.MapPut("/api/agents/controls", (AgentControls request, AgentRuntime runtime, CancellationToken ct) => runtime.ControlsAsync(request, ct));
        app.MapGet("/api/agents/schedules", (IRuntimeReader s, CancellationToken ct) => s.ReadAsync(x => x.AgentSchedules.Values.OrderBy(y => y.CreatedAt).ToList(), ct));
        app.MapPost("/api/agents/schedules", (SaveAgentScheduleRequest request, AgentRuntime runtime, CancellationToken ct) => runtime.SaveScheduleAsync(request, ct));
        app.MapPost("/api/agents/runs", (StartAgentRunRequest request, AgentRuntime runtime, CancellationToken ct) => runtime.StartAsync(request, ct));
        app.MapGet("/api/agents/runs", (IRuntimeReader s, CancellationToken ct) => s.ReadAsync(x => x.AgentRuns.Values.Where(y => y.DeletedAt is null).OrderByDescending(y => y.CreatedAt).Take(30).Select(y => AgentRuntime.View(x, y)).ToList(), ct));
        app.MapPost("/api/agents/runs/{id:guid}/cancel", (Guid id, AgentRuntime runtime, CancellationToken ct) => runtime.CancelAsync(id, false, ct));
        app.MapDelete("/api/agents/runs/{id:guid}", (Guid id, AgentRuntime runtime, CancellationToken ct) => runtime.CancelAsync(id, true, ct));
        app.MapGet("/api/agents/notifications", (IRuntimeReader s, TimeProvider clock, CancellationToken ct) => s.ReadAsync(x => x.AgentOutbox.Values.Where(y => y.Delivered && y.ExpiresAt > clock.GetUtcNow()).OrderByDescending(y => y.CreatedAt).Take(30).ToList(), ct));
        app.MapPost("/api/agents/notifications/{id:guid}/read", (Guid id, IRuntimeStore s, CancellationToken ct) => s.UpdateAsync(x =>
        { Guard.Require(x.AgentOutbox.TryGetValue(id, out var note), 404, "Notification not found."); x.AgentOutbox[id] = note with { Read = true }; return true; }, ct));
    }
}
