using System.Net;
using System.Net.Http.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Forge;
using Jarvis.Orchestrator.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed class ForgeClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-11-30T12:00:00Z");
    public override DateTimeOffset GetUtcNow() => Now;
}

public sealed class ForgeApiFactory(bool enabled = true, ForgeClock? clock = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        TestBudgetSource.Configure(builder); builder.UseEnvironment("Testing");
        builder.UseSetting("Jarvis:ApiKey", JarvisApiFactory.TestKey);
        builder.UseSetting("Jarvis:StorageProvider", "InMemory");
        builder.UseSetting("Forge:Enabled", enabled ? "true" : "false");
        builder.ConfigureTestServices(s => s.AddSingleton<TimeProvider>(clock ?? new ForgeClock()));
    }
    public HttpClient Owner()
    {
        var client = CreateClient(); client.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey);
        TestOwnerManagement.Authorize(client); return client;
    }
}

public sealed class ForgeProgrammeTests
{
    public const string Root = "/api/forge/programmes";
    private static async Task<ForgeProgramme> Create(HttpClient owner, Guid? id = null) =>
        await Post(owner, new CreateForgeProgrammeRequest(id ?? Guid.NewGuid()));
    private static async Task<ForgeProgramme> Post(HttpClient owner, CreateForgeProgrammeRequest request)
    {
        using var response = await owner.PostAsJsonAsync(Root, request); response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ForgeProgramme>())!;
    }

    [Fact]
    public void View_uses_stored_dates_and_rejects_incompatible_phase_configuration()
    {
        var programme = new ForgeProgramme(Guid.NewGuid(), ForgeProgrammePolicy.OwnerScope, 1, 1,
            ForgeCalendar.DefaultStart, ForgeCalendar.DefaultGraduation, ForgeCalendar.DefaultTimeZone, false,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        var view = ForgeProgrammePolicy.View(programme, DateTimeOffset.UtcNow);
        Assert.Equal(programme.Start, view.Calendar.Start); Assert.Equal(programme.Graduation, view.Calendar.Graduation);
        Assert.Throws<ArgumentException>(() => ForgeProgrammePolicy.View(programme with { Start = programme.Start.AddDays(1) }, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Deleted_programmes_do_not_exhaust_active_allowance_or_require_erasing_JARVIS()
    {
        using var host = new ForgeApiFactory(); using var owner = host.Owner(); Guid first = default;
        for (var i = 0; i < 12; i++) {
            var programme = await Create(owner); if (i == 0) first = programme.Id;
            using var deleted = await owner.DeleteAsync($"{Root}/{programme.Id}?revision=1"); deleted.EnsureSuccessStatusCode();
        }
        for (var i = 0; i < 10; i++) await Create(owner);
        using var full = await owner.PostAsJsonAsync(Root, new CreateForgeProgrammeRequest(Guid.NewGuid()));
        Assert.Equal(HttpStatusCode.Conflict, full.StatusCode);
        using var stale = await owner.PostAsJsonAsync(Root, new CreateForgeProgrammeRequest(first));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [Fact]
    public async Task Default_off_returns_404_without_activating_Forge_and_keeps_export_shape_unchanged()
    {
        using var host = new JarvisApiFactory(); using var owner = host.CreateAuthorisedClient(); var id = Guid.NewGuid();
        using var get = await owner.GetAsync(Root); Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        using var post = await owner.PostAsJsonAsync(Root, new CreateForgeProgrammeRequest(id)); Assert.Equal(HttpStatusCode.NotFound, post.StatusCode);
        using var patch = await owner.PatchAsJsonAsync($"{Root}/{id}/preferences", new PauseForgeProgrammeRequest(1, true)); Assert.Equal(HttpStatusCode.NotFound, patch.StatusCode);
        using var delete = await owner.DeleteAsync($"{Root}/{id}?revision=1"); Assert.Equal(HttpStatusCode.NotFound, delete.StatusCode);
        var export = await host.Services.GetRequiredService<IPrivacyStore>().ExportAsync(DateTimeOffset.UtcNow);
        Assert.Null(export.ForgeProgrammes);
        Assert.DoesNotContain("forgeProgrammes", System.Text.Json.JsonSerializer.Serialize(export, LifeScenarioTests.Json));
    }

    [Fact]
    public async Task Create_is_idempotent_and_never_overwrites_the_interview_programme()
    {
        using var host = new ForgeApiFactory(); using var owner = host.Owner();
        using var started = await owner.PostAsJsonAsync("/api/programme", new StartProgrammeRequest(Guid.NewGuid(), "2026-11-01")); started.EnsureSuccessStatusCode();
        var before = await owner.GetStringAsync("/api/programme");
        var programme = await Create(owner);
        Assert.Equal(programme, await Create(owner, programme.Id));
        Assert.Equal(before, await owner.GetStringAsync("/api/programme"));
        Assert.Equal(ForgeProgrammePolicy.OwnerScope, programme.OwnerScope);
        Assert.Equal(1, programme.Revision);
        var view = (await owner.GetFromJsonAsync<ForgeProgrammeView>($"{Root}/{programme.Id}", LifeScenarioTests.Json))!;
        Assert.Equal(ForgeProgrammeStatus.NotStarted, view.Calendar.Status);
        Assert.Equal(261, view.Calendar.ProgrammeDays);
        Assert.Single((await owner.GetFromJsonAsync<ForgeProgrammeView[]>(Root, LifeScenarioTests.Json))!);
        using var mismatch = await owner.PostAsJsonAsync(Root, new CreateForgeProgrammeRequest(programme.Id, TimeZone: "UTC"));
        Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);
    }

    [Fact]
    public async Task Pause_resume_use_revision_fences_and_do_not_reset_calendar_progress()
    {
        var clock = new ForgeClock { Now = DateTimeOffset.Parse("2027-05-31T12:00:00Z") };
        using var host = new ForgeApiFactory(clock: clock); using var owner = host.Owner(); var programme = await Create(owner);
        var runtime = host.Services.GetRequiredService<IRuntimeReader>(); var globalControls = await runtime.ReadAsync(s => s.AgentControls);
        using var paused = await owner.PatchAsJsonAsync($"{Root}/{programme.Id}/preferences", new PauseForgeProgrammeRequest(1, true)); paused.EnsureSuccessStatusCode();
        var result = (await paused.Content.ReadFromJsonAsync<ForgeProgramme>())!; Assert.Equal(2, result.Revision); Assert.True(result.Paused);
        using var stale = await owner.PatchAsJsonAsync($"{Root}/{programme.Id}/preferences", new PauseForgeProgrammeRequest(1, false)); Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var unchanged = await owner.PatchAsJsonAsync($"{Root}/{programme.Id}/preferences", new PauseForgeProgrammeRequest(2, true)); unchanged.EnsureSuccessStatusCode();
        Assert.Equal(2, (await unchanged.Content.ReadFromJsonAsync<ForgeProgramme>())!.Revision);
        using var resumed = await owner.PatchAsJsonAsync($"{Root}/{programme.Id}/preferences", new PauseForgeProgrammeRequest(2, false)); resumed.EnsureSuccessStatusCode();
        var view = (await owner.GetFromJsonAsync<ForgeProgrammeView>($"{Root}/{programme.Id}", LifeScenarioTests.Json))!;
        Assert.False(view.Programme.Paused); Assert.Equal(182, view.Calendar.DayNumber);
        Assert.Equal(globalControls, await runtime.ReadAsync(s => s.AgentControls));
    }

    [Fact]
    public async Task Deleted_identity_cannot_be_recreated_and_export_retains_only_configuration_tombstone()
    {
        using var host = new ForgeApiFactory(); using var owner = host.Owner(); var programme = await Create(owner);
        using var stale = await owner.DeleteAsync($"{Root}/{programme.Id}?revision=9"); Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var removed = await owner.DeleteAsync($"{Root}/{programme.Id}?revision=1"); removed.EnsureSuccessStatusCode();
        Assert.Empty((await owner.GetFromJsonAsync<ForgeProgrammeView[]>(Root, LifeScenarioTests.Json))!);
        using var get = await owner.GetAsync($"{Root}/{programme.Id}"); Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        using var replay = await owner.PostAsJsonAsync(Root, new CreateForgeProgrammeRequest(programme.Id)); Assert.Equal(HttpStatusCode.Conflict, replay.StatusCode);
        var export = await host.Services.GetRequiredService<IPrivacyStore>().ExportAsync(DateTimeOffset.UtcNow);
        Assert.NotNull(Assert.Single(export.ForgeProgrammes!).DeletedAt);
    }

    [Fact]
    public async Task Authenticated_companion_cannot_read_or_create_Forge_programmes()
    {
        using var host = new ForgeApiFactory(); using var owner = host.Owner();
        await host.Services.GetRequiredService<DeviceService>().RegisterAsync(new("forge-phone", "Fixture phone", DeviceKind.AndroidCompanion, new(true, true, false, false, ["audio-only"])));
        var credential = await host.Services.GetRequiredService<DeviceCredentials>().IssueAsync("forge-phone", default);
        using var phone = host.CreateClient(); phone.DefaultRequestHeaders.Add("X-Jarvis-Key", credential.Secret);
        using var get = await phone.GetAsync(Root); Assert.Equal(HttpStatusCode.Forbidden, get.StatusCode);
        using var post = await phone.PostAsJsonAsync(Root, new CreateForgeProgrammeRequest(Guid.NewGuid())); Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
    }

    [Fact]
    public async Task Disabling_feature_blocks_existing_routes_but_not_privacy_export()
    {
        using var host = new ForgeApiFactory(); using var owner = host.Owner(); var programme = await Create(owner);
        host.Services.GetRequiredService<IConfiguration>()["Forge:Enabled"] = "false";
        using var get = await owner.GetAsync(Root); Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        using var patch = await owner.PatchAsJsonAsync($"{Root}/{programme.Id}/preferences", new PauseForgeProgrammeRequest(1, true)); Assert.Equal(HttpStatusCode.NotFound, patch.StatusCode);
        Assert.Single((await host.Services.GetRequiredService<IPrivacyStore>().ExportAsync(DateTimeOffset.UtcNow)).ForgeProgrammes!);
    }

    [Fact]
    public async Task Failed_composite_write_rolls_back_programme_and_runtime_together()
    {
        using var host = new ForgeApiFactory(); using var owner = host.Owner();
        var transaction = host.Services.GetRequiredService<ICoreTransaction>(); var runtime = host.Services.GetRequiredService<IRuntimeReader>();
        var before = await runtime.ReadAsync(s => s.AgentControls);
        var programme = new ForgeProgramme(Guid.NewGuid(), ForgeProgrammePolicy.OwnerScope, 1, 1, ForgeCalendar.DefaultStart, ForgeCalendar.DefaultGraduation,
            ForgeCalendar.DefaultTimeZone, false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<IOException>(() => transaction.ExecuteAsync<bool>(async writes => {
            await writes.SaveForgeProgrammeAsync(programme); writes.Runtime.AgentControls = before with { Paused = !before.Paused };
            throw new IOException("Synthetic pre-commit failure");
        }));
        Assert.Empty(await host.Services.GetRequiredService<IForgeProgrammeReader>().ListAsync(ForgeProgrammePolicy.OwnerScope));
        Assert.Equal(before, await runtime.ReadAsync(s => s.AgentControls));
    }

    [Fact]
    public async Task Erasure_clears_programme_and_blocks_stale_generation_recreation()
    {
        using var host = new ForgeApiFactory(); using var owner = host.Owner(); await Create(owner);
        var privacy = host.Services.GetRequiredService<IPrivacyStore>(); var status = await privacy.StatusAsync();
        var erased = await privacy.EraseAsync(new(Guid.NewGuid(), status.Revision, PrivacyData.ErasePhrase), new string('a', 64), DateTimeOffset.UtcNow);
        Assert.Empty((await privacy.ExportAsync(DateTimeOffset.UtcNow)).ForgeProgrammes ?? []);
        await privacy.ReopenAsync(new(erased.Revision, PrivacyData.ReopenPhrase), new string('b', 64));
        using (PrivacyContext.Enter(status.Epoch)) {
            var failure = await Assert.ThrowsAsync<DomainException>(() => host.Services.GetRequiredService<ForgeProgrammeService>().CreateAsync(new(Guid.NewGuid())));
            Assert.Equal(409, failure.StatusCode);
        }
        Assert.True(erased.Erased);
    }

    [Fact]
    public async Task Concurrent_creation_of_same_operation_returns_one_programme()
    {
        using var host = new ForgeApiFactory(); using var owner = host.Owner(); var id = Guid.NewGuid();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Create(owner, id)));
        Assert.All(results, x => Assert.Equal(results[0], x));
        Assert.Single((await owner.GetFromJsonAsync<ForgeProgrammeView[]>(Root, LifeScenarioTests.Json))!);
    }

    [Theory]
    [InlineData("2026-11-01", "2027-08-18", "Europe/London")]
    [InlineData("2026-12-01", "2027-08-19", "Europe/London")]
    [InlineData("2026-12-01", "2027-08-18", "UTC")]
    [InlineData("invalid", "2027-08-18", "Europe/London")]
    public async Task Official_calendar_configuration_is_not_silently_changed(string start, string end, string zone)
    {
        using var host = new ForgeApiFactory(); using var owner = host.Owner();
        using var response = await owner.PostAsJsonAsync(Root, new CreateForgeProgrammeRequest(Guid.NewGuid(), start, end, zone));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty((await owner.GetFromJsonAsync<ForgeProgrammeView[]>(Root, LifeScenarioTests.Json))!);
    }
}
