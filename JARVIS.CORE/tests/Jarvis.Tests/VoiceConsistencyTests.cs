using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

internal sealed class FailBeforeCoreCommit(ICoreTransaction inner) : ICoreTransaction
{
    public Task<T> ExecuteAsync<T>(Func<ICoreTransactionWrites, Task<T>> operation, CancellationToken ct = default) =>
        inner.ExecuteAsync<T>(async writes =>
        {
            await operation(writes);
            throw new IOException("Injected failure before Core commit.");
        }, ct);
}

// These fixtures exercise commit and revocation. LiveAudioFenceTests advances time to verify freshness and expiry.
internal sealed class VoiceFixtureClock : TimeProvider
{
    private readonly DateTimeOffset now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => now;
    public override long GetTimestamp() => 0;
}

public sealed class VoiceConsistencyTests
{
    [Fact]
    public async Task Failed_transcript_commit_leaves_neither_copy_and_retry_writes_one()
    {
        using var host = new JarvisApiFactory();
        var sessions = host.Services.GetRequiredService<ISessionStore>();
        var runtime = host.Services.GetRequiredService<IRuntimeStore>();
        var now = DateTimeOffset.UtcNow;
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        await sessions.AddAsync(new(sessionId, ActivityType.General, "Voice", SessionStatus.Active, "phone", null, now, now, [],
            Messages: [new(Guid.NewGuid(), "user", "Ordinary text", now)]));
        var failure = new FailBeforeCoreCommit(host.Services.GetRequiredService<ICoreTransaction>());
        var failing = new VoiceTranscriptService(failure, TimeProvider.System);
        await Assert.ThrowsAsync<IOException>(() => failing.RecordAsync(sessionId, turnId, "user", "Captured words", 2.75, default));
        Assert.Empty(await runtime.ReadAsync(s => s.VoiceTurns));
        Assert.Equal("Ordinary text", Assert.Single((await sessions.GetAsync(sessionId))!.Messages!).Text);

        var service = host.Services.GetRequiredService<VoiceTranscriptService>();
        await service.RecordAsync(sessionId, turnId, "user", "Captured words", 2.75, default);
        await service.RecordAsync(sessionId, turnId, "user", "Captured words", 2.75, default);
        await Assert.ThrowsAsync<DomainException>(() => service.RecordAsync(sessionId, turnId, "user", "Different text", 2.75, default));
        var recorded = Assert.Single(await runtime.ReadAsync(s => s.VoiceTurns.Values.ToArray()));
        Assert.Equal(turnId, recorded.Id);
        var message = Assert.Single((await sessions.GetAsync(sessionId))!.Messages!, m => m.IsVoice);
        Assert.Equal(message.At, recorded.At);
        Assert.Equal(2.75, message.SpeechSeconds);
        Assert.Empty(await runtime.ReadAsync(s => s.UsageReservations));
    }

    [Fact]
    public async Task Failed_session_transition_preserves_the_live_fence_until_the_successful_commit()
    {
        using var factory = new JarvisApiFactory();
        using var host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(new VoiceFixtureClock())));
        await VerifySessionAndLeaseRollbackAsync(host.Services);
    }

    internal static async Task VerifySessionAndLeaseRollbackAsync(IServiceProvider services)
    {
        var runtime = services.GetRequiredService<IRuntimeStore>();
        var sessions = services.GetRequiredService<ISessionStore>();
        var audio = services.GetRequiredService<AudioOwnership>();
        var clock = services.GetRequiredService<TimeProvider>();
        var now = clock.GetUtcNow();
        var devices = services.GetRequiredService<DeviceService>();
        await devices.RegisterAsync(new("phone", "Voice phone", DeviceKind.AndroidCompanion, new(true, true, false, false, [])));
        await devices.VerifyAsync("phone");
        var id = Guid.NewGuid();
        await sessions.AddAsync(new(id, ActivityType.General, "Voice", SessionStatus.Active, "phone", null, now, now, []));
        var lease = await audio.StartLocalAsync(id, "phone", default);
        lease = await audio.RenewAsync(lease, default);
        using var fence = new LiveAudioFence(lease, audio, services.GetRequiredService<AudioFenceChanges>(), clock);
        Assert.True(await fence.RefreshAsync(default));
        var transaction = new FailBeforeCoreCommit(services.GetRequiredService<ICoreTransaction>());
        var failing = new SessionLifecycleService(sessions, devices,
            services.GetRequiredService<ISessionNotifier>(), clock, transaction);
        await Assert.ThrowsAsync<IOException>(() => failing.TransitionAsync(id, SessionStatus.Paused, default));
        Assert.Equal(SessionStatus.Active, (await sessions.ReadAudioStateAsync(id))!.Status);
        Assert.Equal(lease, await runtime.ReadAudioLeaseAsync());
        Assert.True(await fence.RefreshAsync(default));

        await services.GetRequiredService<SessionLifecycleService>().TransitionAsync(id, SessionStatus.Paused, default);
        Assert.Equal(SessionStatus.Paused, (await sessions.ReadAudioStateAsync(id))!.Status);
        Assert.False(await fence.RefreshAsync(default));
        var state = await runtime.ReadAsync(s => s);
        Assert.Null(state.AudioLease);
        Assert.Equal(lease, state.AudioDrainLease);
        Assert.Equal(lease.ExpiresAt, state.AudioDrainUntil);
    }
}
