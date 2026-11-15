using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;

namespace Jarvis.Tests;

public sealed class AudioDrainTests
{
    private sealed class Clock : TimeProvider {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    [Fact]
    public async Task Revocation_keeps_exclusion_until_source_acknowledgement_or_original_expiry()
    {
        var clock = new Clock(); var store = new InMemoryRuntimeStore(); var devices = new DeviceService(store, clock);
        foreach (var id in new[] { "first", "second" }) await devices.RegisterAsync(new(id, id, DeviceKind.WindowsAgent, new(true, true, true, false, [])));
        var audio = new AudioOwnership(store, new InMemorySessionStore(), devices, clock);
        var first = await audio.StartCalibrationAsync("first", default);
        await store.UpdateAsync(s => { AudioFencing.Revoke(s); return true; });
        await Assert.ThrowsAsync<DomainException>(() => audio.StartCalibrationAsync("second", default));
        Assert.True(await audio.ReleaseAsync(first, default));
        var second = await audio.StartCalibrationAsync("second", default);
        Assert.False(await audio.ReleaseAsync(first, default));
        await store.UpdateAsync(s => { AudioFencing.Revoke(s); return true; });
        clock.Now = second.ExpiresAt.AddTicks(-1);
        await Assert.ThrowsAsync<DomainException>(() => audio.StartCalibrationAsync("first", default));
        clock.Now = second.ExpiresAt;
        var replacement = await audio.StartCalibrationAsync("first", default);
        Assert.True(replacement.Fence > second.Fence);
        Assert.False(await audio.ReleaseAsync(second, default));
    }
}
