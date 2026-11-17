using System.Threading.RateLimiting;

namespace Jarvis.Api.Security;

public sealed class CoreRequestLimits : IDisposable
{
    internal const string Cloud = "cloud";
    internal const string Voice = "voice";
    internal const int VoicePermits = 40;
    internal const int CloudPermits = 20;
    internal const int AnonymousPermits = 8;

    private readonly FixedWindowRateLimiter anonymous = new(Window(AnonymousPermits));
    private readonly PartitionedRateLimiter<string> voiceStreams = PartitionedRateLimiter.Create<string, string>(deviceId =>
        RateLimitPartition.GetFixedWindowLimiter(deviceId, _ => Window(VoicePermits)));

    internal bool AdmitAnonymous()
    {
        using var request = anonymous.AttemptAcquire();
        return request.IsAcquired;
    }

    internal bool AdmitVoiceStream(string deviceId)
    {
        using var request = voiceStreams.AttemptAcquire(deviceId);
        return request.IsAcquired;
    }

    internal static FixedWindowRateLimiterOptions Window(int permits) => new()
    { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true };

    public void Dispose()
    {
        voiceStreams.Dispose();
        anonymous.Dispose();
    }
}
