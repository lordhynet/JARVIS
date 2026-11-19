using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Microsoft.Extensions.DependencyInjection;
namespace Jarvis.Tests;
public sealed class NativeHandoffTests
{
    [Fact]
    public async Task Native_controls_pin_the_live_conversation_instead_of_a_newer_empty_owned_session()
    {
        using var host = new JarvisApiFactory(); var services = host.Services; await Register(services, "phone", DeviceKind.AndroidCompanion); await Register(services, "laptop", DeviceKind.WindowsAgent);
        var sessions = services.GetRequiredService<SessionService>(); var live = await sessions.CreateAsync(new(ActivityType.General, "Live", "phone"));
        await services.GetRequiredService<AudioOwnership>().StartLocalAsync(live.Id, "phone", default);
        var empty = await sessions.CreateAsync(new(ActivityType.General, "Later empty preparation", "phone")); var control = services.GetRequiredService<CompanionHandoffService>();
        Assert.Equal(live.Id, (await control.ControlAsync(new("phone", Locked: false))).ActiveSessionId);
        await Assert.ThrowsAsync<DomainException>(() => control.ControlAsync(new("phone", "start", empty.Id, TargetDeviceId: "laptop", RequestId: Guid.NewGuid(), Locked: false)));
    }
    [Fact]
    public async Task Spoken_handoff_uses_the_latest_exact_unrestricted_request_and_does_not_guess_between_phones()
    {
        using var host = new JarvisApiFactory(); var services = host.Services; await Register(services, "laptop", DeviceKind.WindowsAgent); await Register(services, "phone", DeviceKind.AndroidCompanion);
        var session = await services.GetRequiredService<SessionService>().CreateAsync(new(ActivityType.General, null, "laptop"));
        await services.GetRequiredService<AudioOwnership>().StartLocalAsync(session.Id, "laptop", default);
        var source = Guid.NewGuid(); await services.GetRequiredService<VoiceTranscriptService>().RecordAsync(session.Id, source, "user", "Continue on my phone.", 2, default);
        using var voice = CoreInvocationContext.Enter(InvocationOrigin.Voice); var tools = services.GetRequiredService<Jarvis.Api.Voice.CoreVoiceTools>();
        var args = JsonSerializer.Serialize(new { utterance_id = source });
        var stale = await tools.ExecuteAsync("core_handoff", args, session.Id, new HashSet<Guid> { source }, default, Guid.NewGuid(), _ => Task.FromResult(true)); Assert.Contains("latest captured", stale);
        var result = await tools.ExecuteAsync("core_handoff", args, session.Id, new HashSet<Guid> { source }, default, source, _ => Task.FromResult(true)); Assert.Contains("waiting_for_acceptance", result);
        await Register(services, "phone-two", DeviceKind.AndroidCompanion);
        var ambiguous = await tools.ExecuteAsync("core_handoff", args, session.Id, new HashSet<Guid> { source }, default, source, _ => Task.FromResult(true)); Assert.Contains("Choose one online target", ambiguous);
        Assert.Single((await services.GetRequiredService<ISessionReader>().GetAsync(session.Id))!.Checkpoints);
        await services.GetRequiredService<IRuntimeStore>().UpdateAsync(s => { s.AudioLease = s.AudioLease! with { Locked = true }; return true; });
        var locked = await tools.ExecuteAsync("core_handoff", args, session.Id, new HashSet<Guid> { source }, default, source, _ => Task.FromResult(true)); Assert.Contains("locked", locked);
    }
    internal static async Task Register(IServiceProvider services, string id, DeviceKind kind) {
        var devices = services.GetRequiredService<DeviceService>(); await devices.RegisterAsync(new(id, id, kind, new(true, true, false, false,
            id.Contains("browser") ? ["browser-companion"] : ["audio-only"]))); await devices.VerifyAsync(id);
    }
    [Theory]
    [InlineData("browser", DeviceKind.WindowsAgent, "phone", DeviceKind.AndroidCompanion)]
    [InlineData("phone", DeviceKind.AndroidCompanion, "browser", DeviceKind.WindowsAgent)]
    [InlineData("laptop", DeviceKind.WindowsAgent, "phone", DeviceKind.AndroidCompanion)]
    public async Task Native_handshake_keeps_one_canonical_session_and_requires_source_stop_before_target_lease(string from, DeviceKind fromKind, string to, DeviceKind toKind)
    {
        using var host = new JarvisApiFactory(); var services = host.Services; await Register(services, from, fromKind); await Register(services, to, toKind);
        var sessions = services.GetRequiredService<ISessionStore>(); var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        await sessions.AddAsync(new(id, ActivityType.General, "PRIVATE TITLE", SessionStatus.Active, from, "PRIVATE TASK", now, now, [], Messages: [new(Guid.NewGuid(), "user", "PRIVATE WORDS", now)]));
        var audio = services.GetRequiredService<AudioOwnership>(); var lease = await audio.StartLocalAsync(id, from, default); var control = services.GetRequiredService<CompanionHandoffService>();
        var start = new CompanionHandoffRequest(from, "start", id, TargetDeviceId: to, RequestId: Guid.NewGuid(), Locked: false);
        var view = await control.ControlAsync(start); var offer = Assert.Single(view.Offers);
        await control.ControlAsync(start); Assert.Single((await sessions.GetAsync(id))!.Checkpoints);
        var metadata = JsonSerializer.Serialize(await control.ControlAsync(new(to, Locked: false))); Assert.DoesNotContain("PRIVATE", metadata);
        await Assert.ThrowsAsync<DomainException>(() => control.ControlAsync(new(to, "ready", id, offer.HandoffId, Locked: true)));
        await Assert.ThrowsAsync<DomainException>(() => control.ControlAsync(new(to, "complete", id, offer.HandoffId, Locked: false)));
        await control.ControlAsync(new(to, "ready", id, offer.HandoffId, Locked: false));
        await Assert.ThrowsAsync<DomainException>(() => control.ControlAsync(new(from, "release", id, offer.HandoffId, Locked: false)));
        Assert.True(await audio.CurrentAsync(lease, default)); await audio.ReleaseAsync(lease, default);
        await control.ControlAsync(new(from, "release", id, offer.HandoffId, Locked: false, AudioStopped: true));
        await control.ControlAsync(new(to, "ready", id, offer.HandoffId, Locked: false)); // Delayed duplicate is harmless.
        await control.ControlAsync(new(to, "complete", id, offer.HandoffId, Locked: false));
        await control.ControlAsync(new(from, "release", id, offer.HandoffId, Locked: false, AudioStopped: true));
        var moved = (await sessions.GetAsync(id))!; Assert.Equal(to, moved.ActiveDeviceId); Assert.Equal("PRIVATE WORDS", Assert.Single(moved.Messages!).Text);
        var targetLease = await audio.StartLocalAsync(id, to, default); Assert.True(targetLease.Fence > lease.Fence); Assert.False(await audio.CurrentAsync(lease, default));
        Assert.Empty(await services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.UsageReservations));
    }
    [Fact]
    public async Task Scoped_native_route_cannot_spoof_another_device_or_read_sessions_and_locked_poll_has_no_targets()
    {
        using var host = new JarvisApiFactory(); await Register(host.Services, "phone", DeviceKind.AndroidCompanion); await Register(host.Services, "other", DeviceKind.WindowsAgent);
        var credential = await host.Services.GetRequiredService<DeviceCredentials>().IssueAsync("phone", default);
        using var client = host.CreateClient(); client.DefaultRequestHeaders.Add("X-Jarvis-Key", credential.Secret);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/sessions")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync(CompanionRoutes.VoiceHandoff, new CompanionHandoffRequest("other", Locked: false))).StatusCode);
        var response = await client.PostAsJsonAsync(CompanionRoutes.VoiceHandoff, new CompanionHandoffRequest("phone", Locked: true));
        response.EnsureSuccessStatusCode(); var view = (await response.Content.ReadFromJsonAsync<CompanionHandoffView>(LifeScenarioTests.Json))!;
        Assert.Empty(view.Targets); Assert.Null(view.ActiveSessionId); Assert.True(response.Headers.CacheControl!.NoStore);
    }
    [Fact]
    public async Task Decline_is_idempotent_and_wrong_device_events_do_not_mutate_the_session()
    {
        using var host = new JarvisApiFactory(); var services = host.Services; foreach (var id in new[] { "phone", "laptop", "stranger" }) await Register(services, id, DeviceKind.AndroidCompanion);
        var session = await services.GetRequiredService<SessionService>().CreateAsync(new(ActivityType.General, null, "phone")); var control = services.GetRequiredService<CompanionHandoffService>();
        var offer = Assert.Single((await control.ControlAsync(new("phone", "start", session.Id, TargetDeviceId: "laptop", RequestId: Guid.NewGuid(), Locked: false))).Offers);
        await Assert.ThrowsAsync<DomainException>(() => control.ControlAsync(new("stranger", "cancel", session.Id, offer.HandoffId)));
        await control.ControlAsync(new("laptop", "cancel", session.Id, offer.HandoffId)); await control.ControlAsync(new("laptop", "cancel", session.Id, offer.HandoffId));
        Assert.Equal(HandoffState.Failed, (await services.GetRequiredService<ISessionReader>().ReadHandoffAsync(session.Id))!.State);
    }
}
