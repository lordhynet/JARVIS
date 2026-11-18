using Jarvis.Contracts;
using Jarvis.Api.Integrations;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;

namespace Jarvis.Api;

public sealed class AgentWorker(AgentRuntime runtime, IAgentExecutor executor, IRuntimeReader reader, IPrivacyReader privacy, IConfiguration configuration, ILogger<AgentWorker> logger, AgentActionService actions) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Jarvis:WorkflowsEnabled", true)) return;
        await Task.WhenAll(Poll(stoppingToken), Work(stoppingToken), Work(stoppingToken));
    }
    private async Task Poll(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(ct))
            try { var status = await privacy.StatusAsync(ct); if (status.Erased) continue; using var scope = PrivacyContext.Enter(status.Epoch); await actions.RefreshAsync(ct); await runtime.TickAsync(ct); } catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) { logger.LogWarning("Agent scheduling tick failed ({Type}).", e.GetType().Name); }
    }
    private async Task Work(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(ct))
        {
            AgentTask? task = null;
            try
            {
                var status = await privacy.StatusAsync(ct); if (status.Erased) continue;
                using var scope = PrivacyContext.Enter(status.Epoch);
                using var invocation = CoreInvocationContext.Enter(InvocationOrigin.Agent);
                task = await runtime.ClaimAsync(ct); if (task is null) continue;
                CoreTelemetry.AgentClaims.Add(1); CoreTelemetry.QueueAge.Record(Math.Max(0, (DateTimeOffset.UtcNow - task.AvailableAt).TotalSeconds));
                using var span = CoreTelemetry.Activities.StartActivity("agent.execute"); span?.SetTag("agent.role", task.Role.ToString());
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct); linked.CancelAfter(TimeSpan.FromMinutes(4));
                var renewal = Renew(task, linked);
                try { var output = await executor.ExecuteAsync(task, linked.Token); await runtime.CompleteAsync(task.Id, task.ClaimToken!.Value, output, null, false, linked.Token); CoreTelemetry.AgentCompletions.Add(1, new KeyValuePair<string, object?>("outcome", "succeeded")); }
                finally { await linked.CancelAsync(); try { await renewal; } catch (OperationCanceledException) { } }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e)
            {
                if (task is null) { logger.LogWarning("Agent claim failed ({Type}).", e.GetType().Name); continue; }
                try
                {
                    if (e is DomainException { RetryAfter: { } retry }) await runtime.RecordRetryAfterAsync(task.Id, retry, ct);
                    var uncertain = await reader.ReadAsync(s => s.UsageReservations.Values.Any(x => x.RunId == task.Id && x.ActualUsd is null), ct);
                    await runtime.CompleteAsync(task.Id, task.ClaimToken!.Value, "", uncertain ? "provider_outcome_uncertain" : e is DomainException d && d.StatusCode == 503 ? "provider_not_configured" : "task_failed_validation", uncertain, ct);
                }
                catch (DomainException) { /* Revoked/expired claims cannot publish. Recovery sees their persisted lease. */ }
            }
        }
    }
    private async Task Renew(AgentTask task, CancellationTokenSource linked)
    {
        try { while (!linked.IsCancellationRequested) { await Task.Delay(10000, linked.Token); await runtime.RenewAsync(task.Id, task.ClaimToken!.Value, linked.Token); } }
        catch (OperationCanceledException) when (linked.IsCancellationRequested) { }
        catch { await linked.CancelAsync(); }
    }
}
