using Jarvis.Orchestrator.Cqrs;
using Jarvis.Orchestrator.Cqrs.Sessions;
using Jarvis.Orchestrator.Cqrs.Tools;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Api;

public sealed class RuntimeWorker(ICommandHandler<ExpireHandoffs, CommandCompleted> sessions,
    ICommandHandler<RecoverInterruptedTools, int> tools, IPrivacyReader privacy, ILogger<RuntimeWorker> logger,
    IRuntimeStore runtime, TimeProvider clock, AudioOwnership audio, IVoiceRepairStore voiceRepair) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var invocation = CoreInvocationContext.Enter(InvocationOrigin.Agent);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        var nextVoiceRepair = clock.GetUtcNow().AddSeconds(10);
        var nextPrune = clock.GetUtcNow().AddMinutes(1);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var status = await privacy.StatusAsync(stoppingToken); if (status.Erased) continue;
                using var scope = PrivacyContext.Enter(status.Epoch);
                await sessions.HandleAsync(new ExpireHandoffs(), stoppingToken);
                await tools.HandleAsync(new RecoverInterruptedTools(), stoppingToken);
                await audio.RepairAsync(stoppingToken);
                if (clock.GetUtcNow() >= nextVoiceRepair) { await voiceRepair.RepairTranscriptsAsync(stoppingToken); nextVoiceRepair = clock.GetUtcNow().AddSeconds(10); }
                if (clock.GetUtcNow() >= nextPrune) {
                    if (await runtime.ReadAsync(s => RuntimeMaintenance.HasExpired(s, clock.GetUtcNow()), stoppingToken))
                        await runtime.UpdateAsync(s => RuntimeMaintenance.Prune(s, clock.GetUtcNow()), stoppingToken);
                    nextPrune = clock.GetUtcNow().AddMinutes(1);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { logger.LogError(ex, "Handoff expiration sweep failed."); }
        }
    }
}
