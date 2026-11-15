using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;
using Jarvis.Api.Integrations;

namespace Jarvis.Api;

public sealed class WorkflowWorker(WorkflowService service, PracticePlanProcessor processor, PaidAiProvider paid,
    IRuntimeReader runtime, IPrivacyReader privacy, IConfiguration configuration, ILogger<WorkflowWorker> logger) : BackgroundService
{
    private readonly string _workerId = "worker-" + Guid.NewGuid().ToString("N");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!configuration.GetValue("Jarvis:WorkflowsEnabled", true)) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            WorkflowRun? claimed = null;
            try
            {
                var status = await privacy.StatusAsync(stoppingToken); if (status.Erased) continue;
                using var scope = PrivacyContext.Enter(status.Epoch);
                claimed = await service.ClaimAsync(_workerId, stoppingToken);
                if (claimed is null || claimed.Status != WorkflowStatus.Running) continue;
                // Unknown persisted versions are terminal failures, never silently reinterpreted.
                if (claimed.Kind is not ("practice-plan" or "interview-brief") || claimed.WorkflowVersion != 1)
                {
                    await service.FailAsync(claimed.Id, claimed.ClaimToken!.Value, false, stoppingToken);
                    continue;
                }
                if (claimed.Kind == "practice-plan")
                {
                    var result = await processor.BuildAsync(stoppingToken);
                    await service.CompleteAsync(claimed.Id, claimed.ClaimToken!.Value, result, stoppingToken);
                }
                else
                {
                    using var turn = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    var renewal = RenewAsync(claimed, turn);
                    try
                    {
                        var brief = await paid.GenerateAsync(claimed, turn.Token);
                        await service.CompleteBriefAsync(claimed.Id, claimed.ClaimToken!.Value, brief, turn.Token);
                    }
                    finally
                    {
                        await turn.CancelAsync();
                        try { await renewal; } catch (OperationCanceledException) { }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) when (claimed?.Kind == "interview-brief" && !stoppingToken.IsCancellationRequested)
            {
                // Never automatically replay a paid request whose billing outcome may be unknown.
                try
                {
                    var uncertain = await runtime.ReadAsync(s => s.UsageReservations.Values.Any(x => x.RunId == claimed.Id && x.ActualUsd is null), stoppingToken);
                    await service.FailAsync(claimed.Id, claimed.ClaimToken!.Value, false, stoppingToken,
                        uncertain ? "needs_reconciliation" : "paid_workflow_failed");
                }
                catch (Exception) { logger.LogWarning("Paid workflow failure could not be saved; its lease will expire."); }
            }
            catch (DomainException ex) when (ex.StatusCode == 409) { /* A cancelled or expired claim is fenced out. */ }
            catch (Exception)
            {
                // Do not log exception messages which may contain connection strings or personal content.
                logger.LogWarning("Workflow step failed; eligible work will be retried. RunId: {RunId}", claimed?.Id);
                if (claimed?.ClaimToken is not Guid token) continue;
                try { await service.FailAsync(claimed.Id, token, true, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception) { logger.LogWarning("Workflow failure could not be saved; lease recovery will retry it."); }
            }
        }
    }

    private async Task RenewAsync(WorkflowRun run, CancellationTokenSource turn)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        try
        {
            while (await timer.WaitForNextTickAsync(turn.Token))
                await service.HeartbeatAsync(run.Id, run.ClaimToken!.Value, turn.Token);
        }
        catch (OperationCanceledException) when (turn.IsCancellationRequested) { }
        catch (Exception) { await turn.CancelAsync(); }
    }
}
