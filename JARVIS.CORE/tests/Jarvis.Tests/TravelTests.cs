using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Travel;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

internal sealed class TravelClock : TimeProvider
{
    public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-16T08:10:00Z"); public override DateTimeOffset GetUtcNow() => Now;
}
internal sealed class TravelPolicyFixture : ITravelDeliveryPolicy { public bool NotifyWhenAgentsPaused { get; set; } = true; }
internal sealed class FakeTravelSource(TravelClock clock) : ITravelSource
{
    public int StatusCalls, CatalogueCalls; public bool Fail; public string Description = "Severe Delays", Reason = "Signal failure"; public int Severity = 6;
    public Func<Task>? BeforeStatus;
    public static readonly TravelLine Jubilee = new("jubilee", "Jubilee", "tube");
    public Task<IReadOnlyList<TravelLine>> CatalogueAsync(CancellationToken ct) { CatalogueCalls++; return Task.FromResult<IReadOnlyList<TravelLine>>([Jubilee, new("northern", "Northern", "tube"), ..Enumerable.Range(1, 20).Select(i => new TravelLine("line" + i, "Fixture line " + i, "tube"))]); }
    public Task<IReadOnlyList<TravelStop>> StopsAsync(IReadOnlyList<string> ids, CancellationToken ct) => Task.FromResult<IReadOnlyList<TravelStop>>(ids.Select(id => new TravelStop(id, "Public station", ["jubilee"])).ToArray());
    public async Task<IReadOnlyList<TravelStatus>> StatusAsync(IReadOnlyList<TravelLine> lines, CancellationToken ct) {
        Interlocked.Increment(ref StatusCalls); if (BeforeStatus is not null) await BeforeStatus(); if (Fail) throw new HttpRequestException("Synthetic TfL outage");
        return lines.Select(l => new TravelStatus(l.Id, l.Name, Severity, Description, Reason, clock.Now)).ToArray();
    }
    public Task<IReadOnlyList<TravelArrival>> ArrivalsAsync(TravelStop stop, CancellationToken ct) => Task.FromResult<IReadOnlyList<TravelArrival>>(
        Enumerable.Range(1, 9).Select(i => new TravelArrival("jubilee", "Jubilee", stop.Id, "Public destination", "Platform 1", i * 60, clock.Now.AddMinutes(i))).ToArray());
}
internal sealed class TravelRig
{
    public TravelClock Clock = new(); public InMemoryRuntimeStore Runtime = new(); public MemoryPrivacyBoundary Boundary = new();
    public FakeTravelSource Source; public MemoryTravelStore Store; public TravelPolicyFixture Policy = new(); public TravelMonitor Monitor; public TravelService Service;
    public TravelRig() { Source = new(Clock); Store = new(Runtime, Boundary); Monitor = new(Store, Source, Clock, Policy); Service = new(Store, Source, Clock); }
    public static TravelRoute Route(string start = "08:00", string end = "10:00", IReadOnlyList<TravelLine>? lines = null, IReadOnlyList<DayOfWeek>? days = null) =>
        new(Guid.NewGuid(), "PRIVATE ROUTE NAME", lines ?? [FakeTravelSource.Jubilee], [new("940GZZLUTST", "Public station", ["jubilee"])],
            [new(days ?? [DayOfWeek.Monday], start, end)], TravelPolicy.London, true, 1, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
    public Task Add(TravelRoute? route = null) => Store.UpdateAsync((s, _) => { var value = route ?? Route(); s.Routes[value.Id] = value; return true; });
    public Task Observe(string description = "Severe Delays", string reason = "Signal failure", int severity = 6) => Store.UpdateAsync((s, runtime) => {
        var statuses = s.Routes.Values.SelectMany(r => r.Lines).DistinctBy(l => l.Id).Select(l => new TravelStatus(l.Id, l.Name, severity, description, reason, Clock.Now)).ToArray();
        TravelMonitor.Observe(s, runtime, statuses, Clock.Now, Policy.NotifyWhenAgentsPaused); return true;
    });
    public Task<int> Notices() => Runtime.ReadAsync(s => s.AgentOutbox.Count);
}
public sealed class TravelTests
{
    [Fact]
    public async Task Travel_voice_is_code_registered_public_read_only_and_cannot_book_or_edit()
    {
        using var root = new JarvisApiFactory(); var source = new FakeTravelSource(new()); using var host = root.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<ITravelSource>(source)));
        var services = host.Services; var tools = services.GetRequiredService<Jarvis.Api.Voice.CoreVoiceTools>();
        var capability = services.GetRequiredService<Jarvis.Orchestrator.Domains.ICapabilityRegistry>().Get(Jarvis.Api.Voice.CoreVoiceTools.TravelName);
        Assert.Equal(Jarvis.Orchestrator.Domains.CapabilityRisk.ReadOnly, capability.Risk); Assert.False(capability.RequiresOwnerApproval);
        Assert.Contains(Jarvis.Contracts.ReasoningDataClass.Public, capability.DataClasses!);
        var status = await tools.ExecuteAsync(Jarvis.Api.Voice.CoreVoiceTools.TravelName, """{"action":"status","route":null,"line":"jubilee","stop":null}""", Guid.NewGuid(), new HashSet<Guid>(), default);
        Assert.True(JsonDocument.Parse(status).RootElement.GetProperty("ok").GetBoolean());
        var refused = await tools.ExecuteAsync(Jarvis.Api.Voice.CoreVoiceTools.TravelName, """{"action":"book","route":null,"line":null,"stop":null}""", Guid.NewGuid(), new HashSet<Guid>(), default);
        Assert.False(JsonDocument.Parse(refused).RootElement.GetProperty("ok").GetBoolean()); Assert.Empty(await services.GetRequiredService<TravelService>().ListAsync());
    }
    [Theory]
    [InlineData("2026-11-16T23:15:00Z", true)]
    [InlineData("2026-11-17T00:59:00Z", true)]
    [InlineData("2026-11-17T01:00:00Z", false)]
    [InlineData("2026-11-17T23:15:00Z", false)]
    public void Crossing_midnight_uses_the_start_weekday(string utc, bool active) => Assert.Equal(active, TravelPolicy.Active(TravelRig.Route("23:00", "01:00"), DateTimeOffset.Parse(utc)));
    [Fact]
    public void London_windows_follow_actual_GMT_BST_minutes_and_repeated_minutes_keep_the_same_occurrence()
    {
        var gap = TravelRig.Route("01:10", "01:20", days: [DayOfWeek.Sunday]);
        Assert.False(TravelPolicy.PollEligible(gap, DateTimeOffset.Parse("2026-03-29T00:15:00Z")));
        Assert.False(TravelPolicy.Active(gap, DateTimeOffset.Parse("2026-03-29T01:15:00Z")));
        var repeated = TravelRig.Route("01:15", "01:45", days: [DayOfWeek.Sunday]);
        var first = DateTimeOffset.Parse("2026-10-25T00:30:00Z"); var second = first.AddHours(1);
        Assert.True(TravelPolicy.Active(repeated, first)); Assert.True(TravelPolicy.Active(repeated, second));
        Assert.Equal(TravelPolicy.Occurrences(repeated, first), TravelPolicy.Occurrences(repeated, second));
        Assert.Equal(DateTimeOffset.Parse("2026-10-25T00:45:00Z"), TravelPolicy.ActiveUntil(repeated, first));
    }
    [Fact]
    public async Task Lookahead_polls_but_never_notifies_early_and_disabled_routes_make_zero_calls()
    {
        var rig = new TravelRig(); await rig.Add(); rig.Clock.Now = DateTimeOffset.Parse("2026-11-16T06:59:00Z");
        Assert.False(await rig.Monitor.PollAsync()); Assert.Equal(0, rig.Source.StatusCalls);
        rig.Clock.Now = rig.Clock.Now.AddMinutes(1); Assert.True(await rig.Monitor.PollAsync());
        rig.Clock.Now = rig.Clock.Now.AddMinutes(2); Assert.True(await rig.Monitor.PollAsync()); Assert.Equal(0, await rig.Notices());
        rig.Clock.Now = DateTimeOffset.Parse("2026-11-16T08:00:00Z"); Assert.True(await rig.Monitor.PollAsync()); Assert.Equal(1, await rig.Notices());
        var notice = Assert.Single(await rig.Runtime.ReadAsync(s => s.AgentOutbox.Values.ToArray())); Assert.DoesNotContain("PRIVATE", notice.Title);
        await rig.Store.UpdateAsync((s, _) => { foreach (var (id, route) in s.Routes.ToArray()) s.Routes[id] = route with { Enabled = false }; return true; });
        rig.Clock.Now = rig.Clock.Now.AddHours(1); Assert.False(await rig.Monitor.PollAsync()); Assert.Equal(3, rig.Source.StatusCalls); Assert.Equal(0, rig.Source.CatalogueCalls);
    }
    [Fact]
    public async Task Transient_blips_failures_and_stale_candidates_cannot_confirm_a_change()
    {
        var rig = new TravelRig(); await rig.Add(); await rig.Observe("Good Service", "", 10); await rig.Observe("Good Service", "", 10);
        await rig.Observe(); await rig.Observe("Good Service", "", 10); Assert.Equal(0, await rig.Notices());
        await rig.Observe(); rig.Clock.Now = rig.Clock.Now.AddHours(1); await rig.Observe(); Assert.Equal(0, await rig.Notices());
        rig.Clock.Now = rig.Clock.Now.AddMinutes(2); await rig.Observe(); Assert.Equal(1, await rig.Notices());
    }
    [Fact]
    public async Task Material_reason_worsening_and_recovery_are_deferred_by_line_cooldown_and_numeric_codes_are_not_ranks()
    {
        var rig = new TravelRig(); await rig.Add(); await rig.Observe("Minor Delays", "Signal failure.", 900); await rig.Observe("Minor Delays", "Signal failure.", 900);
        Assert.Equal(1, await rig.Notices()); await rig.Observe("Minor Delays", " SIGNAL   FAILURE! ", 900); Assert.Equal(1, await rig.Notices());
        await rig.Observe("Severe Delays", "Signal failure", 1); await rig.Observe("Severe Delays", "Signal failure", 1); Assert.Equal(1, await rig.Notices());
        rig.Clock.Now = rig.Clock.Now.AddMinutes(16); await rig.Observe("Severe Delays", "Signal failure", 1); Assert.Equal(2, await rig.Notices());
        await rig.Observe("Good Service", "", 10); await rig.Observe("Good Service", "", 10); Assert.Equal(2, await rig.Notices());
        rig.Clock.Now = rig.Clock.Now.AddMinutes(16); await rig.Observe("Good Service", "", 10); Assert.Equal(3, await rig.Notices());
    }
    [Fact]
    public async Task Six_daily_disruption_notices_are_global_and_recoveries_do_not_spend_that_allowance()
    {
        var rig = new TravelRig(); var lines = Enumerable.Range(1, 12).Select(i => new TravelLine("line" + i, "Public " + i, "tube")).ToArray(); await rig.Add(TravelRig.Route(lines: lines));
        await rig.Observe(); await rig.Observe(); Assert.Equal(6, await rig.Notices());
        rig.Clock.Now = rig.Clock.Now.AddMinutes(16); await rig.Observe("Good Service", "", 10); await rig.Observe("Good Service", "", 10); Assert.Equal(12, await rig.Notices());
        Assert.Equal(6, await rig.Store.ReadAsync(s => s.Monitor.DisruptionNotices));
        rig.Clock.Now = rig.Clock.Now.AddMinutes(16); await rig.Observe(); await rig.Observe(); Assert.Equal(12, await rig.Notices());
    }
    [Fact]
    public async Task Agent_pause_setting_is_respected_and_quiet_bypass_is_bound_to_the_active_window()
    {
        var rig = new TravelRig(); await rig.Add(); await rig.Runtime.UpdateAsync(s => { s.AgentControls = new(AgentsPaused: true, QuietStart: "07:00", QuietEnd: "09:00"); return true; });
        rig.Policy.NotifyWhenAgentsPaused = false; await rig.Observe(); await rig.Observe(); Assert.Equal(0, await rig.Notices());
        rig.Policy.NotifyWhenAgentsPaused = true; await rig.Observe(); Assert.Equal(1, await rig.Notices());
        var notice = Assert.Single(await rig.Runtime.ReadAsync(s => s.AgentOutbox.Values.ToArray())); Assert.True(notice.Delivered); Assert.Equal(NotificationCategories.Travel, notice.Category);
        rig.Clock.Now = DateTimeOffset.Parse("2026-11-16T10:00:00Z"); await rig.Observe("Suspended", "Changed", 2); await rig.Observe("Suspended", "Changed", 2); Assert.Equal(1, await rig.Notices());
    }
    [Fact]
    public async Task Failed_polls_back_off_and_a_concurrent_worker_cannot_duplicate_the_request()
    {
        var rig = new TravelRig(); await rig.Add(); var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); rig.Source.BeforeStatus = () => hold.Task;
        var first = rig.Monitor.PollAsync(); Assert.False(await rig.Monitor.PollAsync()); Assert.Equal(1, rig.Source.StatusCalls); hold.SetResult(); Assert.True(await first);
        rig.Clock.Now = rig.Clock.Now.AddMinutes(2); rig.Source.BeforeStatus = null; rig.Source.Fail = true;
        for (var attempt = 1; attempt <= 4; attempt++) {
            var at = rig.Clock.Now; Assert.False(await rig.Monitor.PollAsync()); var meta = await rig.Store.ReadAsync(s => s.Monitor);
            Assert.Equal(attempt, meta.Failures); var minutes = Math.Min(15, Math.Pow(2, attempt));
            Assert.InRange((meta.NextPollAt!.Value - at).TotalMinutes, minutes, Math.Min(15, minutes * 1.1));
            Assert.False(await rig.Monitor.PollAsync()); rig.Clock.Now = meta.NextPollAt.Value;
        }
    }
    [Fact]
    public async Task Route_replays_limits_revisions_deletion_and_storage_boundary_are_enforced()
    {
        var rig = new TravelRig(); SaveTravelRouteRequest Request(string name) => new(Guid.NewGuid(), null, name, ["jubilee"], ["940GZZLUTST"], [new([DayOfWeek.Monday], "08:00", "10:00")]);
        var request = Request("Private journey"); var saved = await rig.Service.SaveAsync(null, request); Assert.Equal(saved.Id, (await rig.Service.SaveAsync(null, request)).Id);
        await Assert.ThrowsAsync<DomainException>(() => rig.Service.SaveAsync(null, request with { Name = "Different" }));
        await Assert.ThrowsAsync<DomainException>(() => rig.Service.SaveAsync(saved.Id, request with { RequestId = Guid.NewGuid(), ExpectedRevision = 99 }));
        for (var i = 1; i < 10; i++) await rig.Service.SaveAsync(null, Request("Route " + i));
        await Assert.ThrowsAsync<DomainException>(() => rig.Service.SaveAsync(null, Request("Eleventh")));
        Assert.DoesNotContain("Private journey", await rig.Runtime.ReadAsync(s => JsonSerializer.Serialize(s)));
        await rig.Observe(); await rig.Observe(); Assert.True(await rig.Service.DeleteAsync(saved.Id, saved.Revision));
        Assert.All(await rig.Runtime.ReadAsync(s => s.AgentOutbox.Values.Where(n => n.TravelAuthority?.RouteId == saved.Id).ToArray()), n => Assert.True(n.ExpiresAt <= rig.Clock.Now));
        var prior = await rig.Store.ReadAsync(s => s.Routes.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => rig.Store.UpdateAsync<bool>((s, runtime) => { s.Routes.Clear(); runtime.AgentOutbox.Clear(); throw new InvalidOperationException("Synthetic rollback"); }));
        Assert.Equal(prior, await rig.Store.ReadAsync(s => s.Routes.Count));
    }
    [Fact]
    public async Task Voice_results_are_public_while_locked_saved_stop_bounded_and_alert_context_has_a_total_byte_limit()
    {
        var rig = new TravelRig(); var lines = new[] { FakeTravelSource.Jubilee, new TravelLine("northern", "Northern", "tube"), new("line1", "Line 1", "tube"), new("line2", "Line 2", "tube") };
        await rig.Add(TravelRig.Route(lines: lines)); await rig.Observe(); await rig.Observe();
        await Assert.ThrowsAsync<DomainException>(() => rig.Service.VoiceAsync("status", "PRIVATE ROUTE NAME", null, null, true, default));
        await Assert.ThrowsAsync<DomainException>(() => rig.Service.VoiceAsync("status", null, null, null, true, default));
        await Assert.ThrowsAsync<DomainException>(() => rig.Service.VoiceAsync("arrivals", null, null, "940GZZLUTST", true, default));
        var status = JsonSerializer.Serialize(await rig.Service.VoiceAsync("status", null, "jubilee", null, true, default)); Assert.DoesNotContain("PRIVATE ROUTE NAME", status);
        var arrivals = JsonSerializer.SerializeToElement(await rig.Service.VoiceAsync("arrivals", null, null, "940GZZLUTST", false, default)); Assert.Equal(5, arrivals.GetProperty("arrivals").GetArrayLength());
        await Assert.ThrowsAsync<DomainException>(() => rig.Service.VoiceAsync("arrivals", null, null, "490000UNKNOWN", true, default));
        await rig.Store.UpdateAsync((s, _) => { foreach (var (id, line) in s.Lines.ToArray()) s.Lines[id] = line with { Confirmed = line.Confirmed! with { Description = string.Concat(Enumerable.Repeat("🚆", 200)) } }; return true; });
        var context = await rig.Service.PublicContextAsync(default); Assert.InRange(Encoding.UTF8.GetByteCount(context), 1, 300); Assert.DoesNotContain("PRIVATE", context); Assert.DoesNotContain("northern", context);
        rig.Clock.Now = rig.Clock.Now.AddMinutes(5); Assert.Empty(await rig.Service.PublicContextAsync(default));
    }
    [Fact]
    public async Task Travel_CRUD_is_owner_only_and_export_erasure_cover_dedicated_data()
    {
        using var root = new JarvisApiFactory(); var source = new FakeTravelSource(new()); using var host = root.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<ITravelSource>(source)));
        using var owner = host.CreateClient(); owner.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey); TestOwnerManagement.Authorize(owner);
        var request = new SaveTravelRouteRequest(Guid.NewGuid(), null, "Private travel name", ["jubilee"], [], [new([DayOfWeek.Monday], "08:00", "09:00")]);
        using var response = await owner.PostAsJsonAsync("/api/travel/routes", request); response.EnsureSuccessStatusCode();
        var exported = (await owner.GetFromJsonAsync<CoreDataExport>("/api/privacy/export", LifeScenarioTests.Json))!; Assert.Equal("Private travel name", Assert.Single(exported.TravelRoutes!).Name);
        var services = host.Services; await services.GetRequiredService<DeviceService>().RegisterAsync(new("travel-phone", "Fixture", DeviceKind.AndroidCompanion, new(true, true, false, false, [])));
        var credential = await services.GetRequiredService<DeviceCredentials>().IssueAsync("travel-phone", default); using var native = host.CreateClient(); native.DefaultRequestHeaders.Add("X-Jarvis-Key", credential.Secret);
        using var denied = await native.GetAsync("/api/travel/routes"); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        var privacy = (await owner.GetFromJsonAsync<PrivacyStatus>("/api/privacy/status"))!; using var erased = await owner.PostAsJsonAsync("/api/privacy/erase", new EraseCoreRequest(Guid.NewGuid(), privacy.Revision, PrivacyData.ErasePhrase)); erased.EnsureSuccessStatusCode();
        Assert.Empty(services.GetRequiredService<MemoryPrivacyBoundary>().Travel.Routes);
    }
}
