using System.Net;
using System.Net.Http.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed class CompanionVoiceTests
{
    [Fact]
    public async Task Incoming_handoff_waits_for_source_stop_and_keeps_the_canonical_conversation()
    {
        using var factory = new JarvisApiFactory(); var services = factory.Services;
        await Register(services, "source"); await Register(services, "target");
        var sessions = services.GetRequiredService<ISessionStore>(); var lifecycle = services.GetRequiredService<SessionLifecycleService>();
        var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        await sessions.AddAsync(new(id, ActivityType.Communication, "Existing discussion", SessionStatus.Active, "source", "FX story", now, now, []));
        var audio = services.GetRequiredService<AudioOwnership>(); var lease = await audio.StartAsync(new(id, "source", true), default);
        var started = await lifecycle.StartAsync(id, new("source", "target", "Continue FX practice"), default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var preparation = services.GetRequiredService<CompanionVoiceService>().PrepareAsync(new(Guid.NewGuid(), "target", true), timeout.Token);
        while ((await sessions.GetAsync(id))!.Handoff!.State != HandoffState.TargetReady) await Task.Delay(20, timeout.Token);
        Assert.False(preparation.IsCompleted);
        Assert.True(await audio.CurrentAsync(lease.Lease, default));
        await lifecycle.AdvanceAsync(id, "release", new(started.Handoff!.Id, "source"), default);
        var prepared = await preparation;
        Assert.Equal(id, prepared.SessionId);
        var result = (await sessions.GetAsync(id))!;
        Assert.Equal("target", result.ActiveDeviceId); Assert.Equal(HandoffState.Completed, result.Handoff!.State);
        Assert.Equal("FX story", result.CurrentTask); Assert.Single(await sessions.ListAsync());
        Assert.False(await audio.CurrentAsync(lease.Lease, default));
        Assert.Empty(await services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.UsageReservations));
    }

    private static async Task Register(IServiceProvider services, string id) {
        var devices = services.GetRequiredService<DeviceService>();
        await devices.RegisterAsync(new(id, "Audio fixture", DeviceKind.AndroidCompanion, new(true, true, false, false, ["audio-only"])));
        await devices.VerifyAsync(id);
    }
    [Fact]
    public async Task Core_prepares_resumes_and_retries_one_canonical_conversation_without_provider_calls() {
        using var factory = new ProviderFactory(_ => throw new InvalidOperationException("Preparation must not dispatch a provider call."));
        using var client = factory.Client(); await Register(factory.Services, "audio-phone");
        var request = new CompanionVoiceRequest(Guid.NewGuid(), "audio-phone", true);
        var result = await LifeScenarioTests.Post<CompanionVoiceSession>(client, "/api/voice/prepare", request);
        Assert.Equal(result, await LifeScenarioTests.Post<CompanionVoiceSession>(client, "/api/voice/prepare", request));
        var sessions = factory.Services.GetRequiredService<ISessionStore>();
        var session = Assert.Single(await sessions.ListAsync());
        Assert.Equal(ActivityType.General, session.Activity); Assert.Equal("audio-phone", session.ActiveDeviceId); Assert.Single(session.Checkpoints);
        var nextRequest = request with { RequestId = Guid.NewGuid() };
        Assert.Equal(result, await LifeScenarioTests.Post<CompanionVoiceSession>(client, "/api/voice/prepare", nextRequest));
        await factory.Services.GetRequiredService<SessionLifecycleService>().TransitionAsync(result.SessionId, SessionStatus.Paused, default);
        Assert.Equal(result, await LifeScenarioTests.Post<CompanionVoiceSession>(client, "/api/voice/prepare", nextRequest));
        Assert.Equal(SessionStatus.Active, (await sessions.GetAsync(result.SessionId))!.Status);
        await factory.Services.GetRequiredService<SessionLifecycleService>().TransitionAsync(result.SessionId, SessionStatus.Ended, default);
        using var endedRetry = await client.PostAsJsonAsync("/api/voice/prepare", nextRequest); Assert.Equal(HttpStatusCode.Conflict, endedRetry.StatusCode);
        Assert.Single(await sessions.ListAsync());
        Assert.Empty(await factory.Services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.UsageReservations));
    }
    [Fact]
    public async Task Preparation_requires_real_device_verification_and_explicit_consent() {
        using var factory = new JarvisApiFactory(); var service = factory.Services.GetRequiredService<CompanionVoiceService>();
        var devices = factory.Services.GetRequiredService<DeviceService>();
        await devices.RegisterAsync(new("unchecked", "Fixture", DeviceKind.AndroidCompanion, new(true, true, false, false, [])));
        await Assert.ThrowsAsync<DomainException>(() => service.PrepareAsync(new(Guid.NewGuid(), "unchecked", true), default));
        await devices.VerifyAsync("unchecked");
        await Assert.ThrowsAsync<DomainException>(() => service.PrepareAsync(new(Guid.NewGuid(), "unchecked", false), default));
        Assert.Empty(await factory.Services.GetRequiredService<ISessionStore>().ListAsync());
    }
    [Fact]
    public async Task Another_devices_live_lease_is_not_stolen_by_companion_preparation() {
        using var factory = new JarvisApiFactory(); var services = factory.Services;
        await Register(services, "first-phone"); await Register(services, "second-phone");
        var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        await services.GetRequiredService<ISessionStore>().AddAsync(new(id, ActivityType.General, "Other conversation", SessionStatus.Active, "first-phone", null, now, now, []));
        await services.GetRequiredService<AudioOwnership>().StartAsync(new(id, "first-phone", true), default);
        await Assert.ThrowsAsync<DomainException>(() => services.GetRequiredService<CompanionVoiceService>().PrepareAsync(new(Guid.NewGuid(), "second-phone", true), default));
        Assert.Single(await services.GetRequiredService<ISessionStore>().ListAsync());
    }
    [Fact]
    public async Task Wake_candidates_are_arbitrated_by_core_audio_fence_with_only_one_winner() {
        using var factory = new ProviderFactory(_ => throw new InvalidOperationException("Wake arbitration must not make a provider request."));
        using var client = factory.Client(); var services = factory.Services;
        await Register(services, "wake-phone-a"); await Register(services, "wake-phone-b");
        var winner = await DomainRegistryTests.Post<WakeCandidateResponse>(client, "/api/voice/wake-candidates",
            new WakeCandidateRequest(Guid.NewGuid(), "wake-phone-a", true));
        Assert.NotEqual(Guid.Empty, winner.SessionId); Assert.Equal(winner.SessionId, winner.Voice.Lease.SessionId);
        Assert.Equal("wake-phone-a", winner.Voice.Lease.DeviceId); Assert.NotEmpty(winner.Voice.Ticket);
        using var loser = await client.PostAsJsonAsync("/api/voice/wake-candidates", new WakeCandidateRequest(Guid.NewGuid(), "wake-phone-b", true));
        Assert.Equal(HttpStatusCode.Conflict, loser.StatusCode);
        Assert.Single(await services.GetRequiredService<ISessionReader>().ListPreparationAsync(default));
        Assert.Equal(winner.Voice.Lease.Fence, (await services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.AudioLease))!.Fence);
    }
    [Fact]
    public async Task Parallel_retries_do_not_create_duplicate_preparation_checkpoints() {
        using var factory = new JarvisApiFactory(); await Register(factory.Services, "phone");
        var service = factory.Services.GetRequiredService<CompanionVoiceService>(); var request = new CompanionVoiceRequest(Guid.NewGuid(), "phone", true);
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.PrepareAsync(request, default)));
        Assert.Single(results.Select(x => x.SessionId).Distinct());
        Assert.Single((Assert.Single(await factory.Services.GetRequiredService<ISessionStore>().ListAsync())).Checkpoints);
    }
    [Fact]
    public async Task Unconfigured_voice_fails_before_creating_a_conversation() {
        using var factory = new JarvisApiFactory(); using var client = factory.CreateAuthorisedClient(); await Register(factory.Services, "phone");
        using var response = await client.PostAsJsonAsync("/api/voice/prepare", new CompanionVoiceRequest(Guid.NewGuid(), "phone", true));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty(await factory.Services.GetRequiredService<ISessionStore>().ListAsync());
    }
}
