using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Api.Privacy;

public sealed class RetentionSettings(IConfiguration config)
{
    public RetentionPolicy Read()
    {
        var policy = config.GetSection("Privacy:Retention").Get<RetentionPolicy>() ?? new(); PrivacyData.Validate(policy); return policy;
    }
}
public sealed class RetentionWorker(IPrivacyStore store, RetentionSettings settings, TimeProvider clock, ILogger<RetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = 60;
            try
            {
                var policy = settings.Read(); delay = policy.SweepMinutes;
                if (policy.Enabled)
                {
                    var status = await store.StatusAsync(stoppingToken);
                    if (!status.Erased)
                    {
                        using var scope = PrivacyContext.Enter(status.Epoch);
                        var now = clock.GetUtcNow(); var at = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
                        var hash = PrivacyData.PolicyHash(policy); var id = new Guid(Convert.FromHexString(PrivacyData.Hash("retention:" + at.ToString("O") + ":" + hash))[..16]);
                        await store.RetainAsync(new(id, at, hash, PrivacyData.RetentionPhrase), policy, now, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (DomainException error) when (error.StatusCode is 409 or 503) { /* A data boundary was crossed. A later tick rechecks policy and epoch. */ }
            catch (Exception error) { logger.LogWarning("Retention sweep failed ({Type}).", error.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromMinutes(delay), stoppingToken); } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
