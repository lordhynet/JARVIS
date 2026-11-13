using Jarvis.Orchestrator.Cqrs;
using Jarvis.Orchestrator.Cqrs.Sessions;
using Jarvis.Orchestrator.Cqrs.Tools;

namespace Jarvis.Api;

public sealed class RuntimeWorker(ICommandHandler<ExpireHandoffs, CommandCompleted> sessions,
    ICommandHandler<RecoverInterruptedTools, int> tools, ILogger<RuntimeWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await sessions.HandleAsync(new ExpireHandoffs(), stoppingToken);
                await tools.HandleAsync(new RecoverInterruptedTools(), stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Handoff expiration sweep failed."); }
        }
    }
}
