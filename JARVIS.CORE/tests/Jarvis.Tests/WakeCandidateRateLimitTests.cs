using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Tests;

public sealed class WakeCandidateRateLimitTests
{
    [Fact]
    public void Caps_each_device_at_six_attempts_per_rolling_ten_minutes()
    {
        var state = new RuntimeState(); var now = DateTimeOffset.Parse("2026-11-15T12:00:00Z");
        for (var i = 0; i < WakeCandidateRules.MaximumPerDevice; i++) WakeCandidateRules.RecordAttempt(state, "phone-a", now);
        var limited = Assert.Throws<DomainException>(() => WakeCandidateRules.RecordAttempt(state, "phone-a", now));
        Assert.Equal(429, limited.StatusCode);
        WakeCandidateRules.RecordAttempt(state, "phone-b", now);

        WakeCandidateRules.RecordAttempt(state, "phone-a", now + WakeCandidateRules.Window);
        Assert.Single(state.WakeCandidateAttempts["phone-a"]);
    }
}
