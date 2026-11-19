using Jarvis.Orchestrator.Recordings;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Api.Privacy;

public sealed class RecordingRetentionWorker(RecordingService recordings, IPrivacyReader privacy, ILogger<RecordingRetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var invocation = CoreInvocationContext.Enter(InvocationOrigin.Agent);
        while (!stoppingToken.IsCancellationRequested) {
            try {
                var status = await privacy.StatusAsync(stoppingToken);
                if (!status.Erased) { using var epoch = PrivacyContext.Enter(status.Epoch); await recordings.RetainAudioAsync(stoppingToken); }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogWarning("Recording audio retention failed ({Type}).", error.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromHours(1), stoppingToken); } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
