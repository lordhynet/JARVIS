using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jarvis.Api;
using Jarvis.Api.Notifications;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Travel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Jarvis.Tests;

public sealed class TravelPushTests
{
    private sealed class Sender : IPushSender
    {
        public List<string> Payloads = [];
        public Task SendAsync(BrowserPushSubscription subscription, Guid noticeId, CancellationToken ct) => throw new InvalidOperationException("The dispatcher must supply the category-aware notice.");
        public Task SendAsync(BrowserPushSubscription subscription, AgentNotification notice, CancellationToken ct) { lock (Payloads) Payloads.Add(WebPushSender.Payload(notice.Id, notice)); return Task.CompletedTask; }
    }
    private static IConfigurationRoot Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> {
        ["Push:Enabled"] = "true", ["Push:PublicKey"] = "fixture", ["Push:PrivateKey"] = "fixture", [ConfigKeys.CoreApiKey] = "fixture-key" }).Build();
    private static Task Subscribe(TravelRig rig) => rig.Runtime.UpdateAsync(s => { var id = Guid.NewGuid(); s.PushSubscriptions[id] = new(id, "https://fcm.googleapis.com/fcm/send/fixture", "fixture", "fixture", PrivacyData.Hash("fixture-key"), rig.Clock.Now); return true; });
    [Fact]
    public async Task Only_authorized_travel_bypasses_quiet_and_pause_and_every_other_category_keeps_a_private_body()
    {
        var rig = new TravelRig(); await rig.Add(); await Subscribe(rig); var sender = new Sender(); var config = Config();
        await rig.Runtime.UpdateAsync(s => { s.AgentControls = s.AgentControls with { AgentsPaused = true, QuietStart = "00:00", QuietEnd = "23:59" }; return true; });
        await rig.Observe(); await rig.Observe(); var travel = Assert.Single(await rig.Runtime.ReadAsync(s => s.AgentOutbox.Values.ToArray()));
        var push = new PushNotifications(rig.Runtime, config, sender, rig.Clock);
        config["Travel:NotifyWhenAgentsPaused"] = "false"; Assert.False(await push.DispatchOneAsync(default));
        config["Travel:NotifyWhenAgentsPaused"] = "true"; await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => push.DispatchOneAsync(default)));
        var payload = Assert.Single(sender.Payloads); Assert.Contains("JARVIS travel", payload); Assert.Contains("Severe Delays", payload); Assert.DoesNotContain("PRIVATE ROUTE", payload);
        Assert.False(await push.DispatchOneAsync(default));
        foreach (var category in new string?[] { null, NotificationCategories.Agent, "Unknown" }) {
            var personal = travel with { Category = category, Title = "PRIVATE CONTENT", PublicSummary = "PRIVATE SUMMARY" };
            Assert.Contains(WebPushSender.PrivateBody, WebPushSender.Payload(personal.Id, personal)); Assert.DoesNotContain("PRIVATE CONTENT", WebPushSender.Payload(personal.Id, personal)); Assert.DoesNotContain("PRIVATE SUMMARY", WebPushSender.Payload(personal.Id, personal));
            Assert.False(await rig.Runtime.ReadAsync(s => PushEligibility.Allowed(s, personal, rig.Clock.Now, true)));
        }
        var malformed = travel with { TravelAuthority = null, PublicSummary = "PRIVATE SUMMARY" }; Assert.Contains(WebPushSender.PrivateBody, WebPushSender.Payload(malformed.Id, malformed));
        Assert.False(await rig.Runtime.ReadAsync(s => PushEligibility.Allowed(s, malformed, rig.Clock.Now, true)));
    }
    [Fact]
    public async Task Route_end_edit_delete_and_lookahead_never_authorize_late_or_early_push()
    {
        var rig = new TravelRig(); var route = TravelRig.Route("08:00", "08:12"); await rig.Add(route); await Subscribe(rig); await rig.Observe(); await rig.Observe();
        var push = new PushNotifications(rig.Runtime, Config(), new Sender(), rig.Clock); rig.Clock.Now = DateTimeOffset.Parse("2026-11-16T08:12Z"); Assert.False(await push.DispatchOneAsync(default));
        rig.Clock.Now = DateTimeOffset.Parse("2026-11-16T08:10Z"); await rig.Service.DeleteAsync(route.Id, route.Revision); Assert.False(await push.DispatchOneAsync(default));
        var early = new TravelRig(); early.Clock.Now = DateTimeOffset.Parse("2026-11-16T07:30Z"); await early.Add(); await Subscribe(early); await early.Observe(); await early.Observe();
        Assert.False(await new PushNotifications(early.Runtime, Config(), new Sender(), early.Clock).DispatchOneAsync(default));
        await rig.Runtime.UpdateAsync(s => { s.AgentControls = s.AgentControls with { AgentsPaused = false, QuietStart = "00:00", QuietEnd = "00:00" }; var id = Guid.NewGuid(); s.AgentOutbox[id] = new(id, Guid.NewGuid(), "Private", rig.Clock.Now, rig.Clock.Now.AddMinutes(1), rig.Clock.Now.AddMinutes(5), Delivered: true); return true; });
        Assert.False(await push.DispatchOneAsync(default));
    }
    [Fact]
    public async Task Simulation_replays_do_not_confirm_twice_and_recovery_uses_the_same_outbox_and_push_path()
    {
        var rig = new TravelRig(); await rig.Add(); await Subscribe(rig); using var invocation = CoreInvocationContext.Enter(InvocationOrigin.ManualClient);
        var request = new TravelSimulationRequest(Guid.NewGuid(), "jubilee", "severe"); var first = await rig.Monitor.SimulateAsync(request); Assert.Empty(first.NotificationIds);
        Assert.True((await rig.Monitor.SimulateAsync(request)).Replayed); Assert.Equal(1, await rig.Store.ReadAsync(s => s.Lines["jubilee"].CandidateCount));
        var second = await rig.Monitor.SimulateAsync(request with { RequestId = Guid.NewGuid() }); Assert.Single(second.NotificationIds); Assert.Equal(1, await rig.Notices());
        await Assert.ThrowsAsync<DomainException>(() => rig.Monitor.SimulateAsync(request with { State = "good" }));
        var sender = new Sender(); var push = new PushNotifications(rig.Runtime, Config(), sender, rig.Clock); Assert.True(await push.DispatchOneAsync(default));
        rig.Clock.Now = rig.Clock.Now.AddMinutes(16);
        await rig.Monitor.SimulateAsync(new(Guid.NewGuid(), "jubilee", "good")); await rig.Monitor.SimulateAsync(new(Guid.NewGuid(), "jubilee", "good"));
        Assert.True(await push.DispatchOneAsync(default)); Assert.Equal(2, sender.Payloads.Count); Assert.Contains("recovery", sender.Payloads[1]); Assert.All(sender.Payloads, payload => Assert.Contains("Simulation:", payload)); Assert.False(await push.DispatchOneAsync(default)); Assert.Equal(0, rig.Source.StatusCalls);
        using var external = CoreInvocationContext.Enter(InvocationOrigin.ExternalContent); await Assert.ThrowsAsync<DomainException>(() => rig.Monitor.SimulateAsync(new(Guid.NewGuid(), "jubilee", "good")));
    }
    private sealed class EnvironmentStub : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Production"; public string ApplicationName { get; set; } = "Fixture"; public string ContentRootPath { get; set; } = ""; public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    [Theory]
    [InlineData(false, false, HttpStatusCode.Forbidden)]
    [InlineData(true, true, HttpStatusCode.Forbidden)]
    [InlineData(true, false, HttpStatusCode.OK)]
    public async Task Simulation_is_owner_only_explicitly_opted_in_and_always_refused_in_Production(bool enabled, bool production, HttpStatusCode expected)
    {
        using var root = new JarvisApiFactory(); var clock = new TravelClock();
        using var host = root.WithWebHostBuilder(b => { b.UseSetting("Travel:AllowSimulation", enabled.ToString()); b.ConfigureServices(s => { s.AddSingleton<TimeProvider>(clock); if (production) s.AddSingleton<IHostEnvironment>(new EnvironmentStub()); }); });
        var services = host.Services; await services.GetRequiredService<ITravelStore>().UpdateAsync((s, _) => { var route = TravelRig.Route(); s.Routes[route.Id] = route; return true; });
        var request = new TravelSimulationRequest(Guid.NewGuid(), "jubilee", "severe"); using var owner = host.CreateClient(); owner.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey);
        using var response = await owner.PostAsJsonAsync("/api/travel/simulate", request); Assert.Equal(expected, response.StatusCode);
        using var anonymous = host.CreateClient(); using var refused = await anonymous.PostAsJsonAsync("/api/travel/simulate", request); Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        await services.GetRequiredService<DeviceService>().RegisterAsync(new("simulation-phone", "Fixture", DeviceKind.AndroidCompanion, new(true, true, false, false, [])));
        var credential = await services.GetRequiredService<DeviceCredentials>().IssueAsync("simulation-phone", default); using var native = host.CreateClient(); native.DefaultRequestHeaders.Add("X-Jarvis-Key", credential.Secret);
        using var denied = await native.PostAsJsonAsync("/api/travel/simulate", request); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        if (expected != HttpStatusCode.OK) Assert.Empty(await services.GetRequiredService<ITravelStore>().ReadAsync(s => s.Lines.Values.ToArray()));
    }
}
