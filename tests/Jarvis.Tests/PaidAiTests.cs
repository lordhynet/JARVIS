using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Jarvis.Api.Integrations;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed class PaidAiTests
{
    [Fact]
    public async Task Unexpected_usage_is_recorded_and_blocks_further_paid_dispatch()
    {
        var store = new InMemoryRuntimeStore(); var budget = new UsageBudget(store, TimeProvider.System, new TestBudgetSource(TimeProvider.System));
        var id = Guid.NewGuid();
        await budget.ReserveAsync(id, Guid.NewGuid(), "test", .01m, new(1, 50, 15), default);
        var actual = await budget.SettleAsync(id, .02m, 1000, 1000, default);
        Assert.True(actual.ReconciliationRequired); Assert.Equal(.02m, actual.ActualUsd);
        var error = await Assert.ThrowsAsync<DomainException>(() => budget.ReserveAsync(Guid.NewGuid(), Guid.NewGuid(), "test", .01m, new(1, 50, 15), default));
        Assert.Equal(503, error.StatusCode);
    }
    [Fact]
    public async Task Concurrent_reservations_cannot_exceed_budget_and_unknown_charges_stay_reserved()
    {
        var store = new InMemoryRuntimeStore(); var service = new UsageBudget(store, TimeProvider.System, new TestBudgetSource(TimeProvider.System));
        var attempts = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            try { await service.ReserveAsync(Guid.NewGuid(), Guid.NewGuid(), "test", .1m, new(.5m, 50m, 15m), default); return true; }
            catch (DomainException ex) when (ex.StatusCode == 429) { return false; }
        }));
        Assert.Equal(5, attempts.Count(x => x));
        Assert.Equal(.5m, await store.ReadAsync(s => s.UsageReservations.Values.Sum(x => x.ReservedUsd)));
        var entry = await store.ReadAsync(s => s.UsageReservations.Values.First());
        await service.SettleAsync(entry.Id, .01m, 100, 100, default);
        Assert.Equal(.41m, await store.ReadAsync(s => s.UsageReservations.Values.Sum(x => x.ActualUsd ?? x.ReservedUsd)));
        await Assert.ThrowsAsync<DomainException>(() => service.ReserveAsync(entry.Id, entry.RunId, "test", .01m, new(1, 50, 15), default));
    }

    [Fact]
    public async Task Unconfigured_provider_rejects_paid_work_and_does_not_dispatch()
    {
        using var host = new JarvisApiFactory(); using var client = host.CreateAuthorisedClient();
        using var response = await client.PostAsJsonAsync("/api/workflows/interview-briefs", new CreateInterviewBriefRequest(Guid.NewGuid(), true));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Empty(await host.Services.GetRequiredService<IWorkflowReader>().ListAsync());
        Assert.Empty(await host.Services.GetRequiredService<IRuntimeReader>().ReadAsync(s => s.UsageReservations));
    }

    [Fact]
    public async Task Paid_brief_sends_bounded_source_context_without_tools_and_validates_citations()
    {
        var sourceId = Guid.NewGuid(); var requests = 0;
        using var host = new PaidFactory(async request =>
        {
            Interlocked.Increment(ref requests);
            Assert.Equal("https://api.anthropic.com/v1/messages", request.RequestUri!.AbsoluteUri);
            Assert.Equal("fixture-anthropic-key", Assert.Single(request.Headers.GetValues("x-api-key")));
            Assert.Equal("2023-06-01", Assert.Single(request.Headers.GetValues("anthropic-version")));
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>();
            Assert.False(body.TryGetProperty("tools", out _));
            Assert.Equal(2048, body.GetProperty("max_tokens").GetInt32());
            Assert.Contains("untrusted", body.GetProperty("system").GetString());
            Assert.DoesNotContain("Secret reference answer", body.ToString());
            return Response(sourceId);
        });
        var run = await Prepare(host, sourceId);
        var provider = host.Services.GetRequiredService<PaidAiProvider>();
        var result = await provider.GenerateAsync(run, default);
        Assert.Equal(3, result.Drills.Count); Assert.Equal(sourceId, Assert.Single(result.SourceIds)); Assert.Equal(1, requests);
        var reservation = Assert.Single(await host.Services.GetRequiredService<IRuntimeReader>().ReadAsync(s => s.UsageReservations.Values.ToList()));
        Assert.NotNull(reservation.ActualUsd); Assert.True(reservation.ActualUsd < reservation.ReservedUsd);
        await Assert.ThrowsAsync<DomainException>(() => provider.GenerateAsync(run, default));
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task Timeout_keeps_conservative_charge_and_missing_consent_never_enqueues()
    {
        using var host = new PaidFactory(_ => throw new HttpRequestException("Synthetic provider timeout"));
        var service = host.Services.GetRequiredService<WorkflowService>();
        await Assert.ThrowsAsync<DomainException>(() => service.CreateBriefAsync(new(Guid.NewGuid(), false)));
        var run = await Prepare(host, Guid.NewGuid());
        await Assert.ThrowsAsync<HttpRequestException>(() => host.Services.GetRequiredService<PaidAiProvider>().GenerateAsync(run, default));
        var reservation = Assert.Single(await host.Services.GetRequiredService<IRuntimeReader>().ReadAsync(s => s.UsageReservations.Values.ToList()));
        Assert.Null(reservation.ActualUsd); Assert.True(reservation.ReservedUsd > 0);
    }

    [Fact]
    public async Task Invented_source_ids_are_rejected_and_budget_is_charged_for_the_provider_response()
    {
        using var host = new PaidFactory(_ => Task.FromResult(Response(Guid.NewGuid())));
        var run = await Prepare(host, Guid.NewGuid());
        var error = await Assert.ThrowsAsync<DomainException>(() => host.Services.GetRequiredService<PaidAiProvider>().GenerateAsync(run, default));
        Assert.Equal(502, error.StatusCode);
        Assert.Null((await host.Services.GetRequiredService<IWorkflowReader>().GetAsync(run.Id))!.Brief);
        var reservation = Assert.Single(await host.Services.GetRequiredService<IRuntimeReader>().ReadAsync(s => s.UsageReservations.Values.ToList()));
        Assert.NotNull(reservation.ActualUsd);
    }

    [Fact]
    public async Task Worker_publishes_one_canonical_paid_brief()
    {
        var sourceId = Guid.NewGuid(); var calls = 0;
        using var original = new PaidFactory(_ => { Interlocked.Increment(ref calls); return Task.FromResult(Response(sourceId)); });
        using var host = original.WithWebHostBuilder(b => b.UseSetting("Jarvis:WorkflowsEnabled", "true"));
        await host.Services.GetRequiredService<IRuntimeStore>().UpdateAsync(s =>
        {
            var now = DateTimeOffset.UtcNow;
            s.Learning[sourceId] = new(sourceId, LearningDomain.SystemDesign, "Explain idempotency", "Secret reference answer", 1, 5, now, now);
            return true;
        });
        var run = await host.Services.GetRequiredService<WorkflowService>().CreateBriefAsync(new(Guid.NewGuid(), true));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var reader = host.Services.GetRequiredService<IWorkflowReader>();
        WorkflowRun? completed;
        while (!WorkflowService.Terminal((completed = await reader.GetAsync(run.Id, deadline.Token))!.Status)) await Task.Delay(100, deadline.Token);
        Assert.Equal(WorkflowStatus.Succeeded, completed.Status); Assert.NotNull(completed.Brief); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Restart_recovery_does_not_replay_an_uncertain_paid_attempt()
    {
        var calls = 0;
        using var host = new PaidFactory(_ => { Interlocked.Increment(ref calls); throw new HttpRequestException("Synthetic lost acknowledgement"); });
        var run = await Prepare(host, Guid.NewGuid());
        var provider = host.Services.GetRequiredService<PaidAiProvider>();
        await Assert.ThrowsAsync<HttpRequestException>(() => provider.GenerateAsync(run, default));
        var store = host.Services.GetRequiredService<IWorkflowStore>();
        await store.UpdateAsync(run.Id, r => r with { LeaseExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1), AvailableAt = DateTimeOffset.UtcNow.AddSeconds(-1), Revision = r.Revision + 1 });
        var recovered = (await host.Services.GetRequiredService<WorkflowService>().ClaimAsync("recovered"))!;
        var error = await Assert.ThrowsAsync<DomainException>(() => provider.GenerateAsync(recovered, default));
        Assert.Equal(409, error.StatusCode); Assert.Equal(1, calls);
        Assert.Single(await host.Services.GetRequiredService<IRuntimeReader>().ReadAsync(s => s.UsageReservations.Values.ToList()));
    }

    [Fact]
    public async Task Workflow_deletion_removes_payload_but_preserves_duplicate_request_identity()
    {
        using var host = new PaidFactory(_ => Task.FromResult(Response(Guid.NewGuid())));
        var run = await Prepare(host, Guid.NewGuid());
        var service = host.Services.GetRequiredService<WorkflowService>();
        await service.CompleteBriefAsync(run.Id, run.ClaimToken!.Value, new("Private draft", ["one", "two", "three"], [], "test-model", DateTimeOffset.UtcNow));
        using var client = host.CreateClient(); client.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey);
        using var deleted = await client.DeleteAsync($"/api/workflows/{run.Id}"); Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var missing = await client.GetAsync($"/api/workflows/{run.Id}"); Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var tombstone = (await host.Services.GetRequiredService<IWorkflowReader>().GetAsync(run.Id))!;
        Assert.Null(tombstone.Brief); Assert.Null(tombstone.Result); Assert.NotNull(tombstone.DeletedAt);
        await Assert.ThrowsAsync<DomainException>(() => service.CreateBriefAsync(new(run.Id, true)));
        Assert.Empty(await host.Services.GetRequiredService<IWorkflowReader>().ListAsync());
    }

    private static async Task<WorkflowRun> Prepare(WebApplicationFactory<Program> host, Guid sourceId)
    {
        await host.Services.GetRequiredService<IRuntimeStore>().UpdateAsync(s =>
        {
            var now = DateTimeOffset.UtcNow;
            s.Learning[sourceId] = new(sourceId, LearningDomain.SystemDesign, "Explain idempotency", "Secret reference answer", 1, 5, now, now);
            return true;
        });
        var service = host.Services.GetRequiredService<WorkflowService>();
        await service.CreateBriefAsync(new(Guid.NewGuid(), true));
        return (await service.ClaimAsync("provider-fixture"))!;
    }

    private static HttpResponseMessage Response(Guid sourceId) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { stop_reason = "end_turn", usage = new { input_tokens = 100, output_tokens = 100 },
            content = new[] { new { type = "text", text = JsonSerializer.Serialize(new { summary = "Practise a concrete ownership example.",
                drills = new[] { "What did you own?", "Which trade-off did you choose?", "How did recovery work?" }, sourceIds = new[] { sourceId } }) } } }), Encoding.UTF8, "application/json")
    };

    private sealed class PaidFactory(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            TestBudgetSource.Configure(builder); builder.UseEnvironment("Testing"); builder.UseSetting("Jarvis:ApiKey", JarvisApiFactory.TestKey);
            builder.UseSetting("Jarvis:StorageProvider", "InMemory"); builder.UseSetting("Jarvis:WorkflowsEnabled", "false");
            builder.UseSetting("Anthropic:ApiKey", "fixture-anthropic-key"); builder.UseSetting("Anthropic:Model", "test-model");
            builder.UseSetting("PaidAI:Enabled", "true"); builder.UseSetting("PaidAI:DailyLimitUsd", "1");
            builder.UseSetting("PaidAI:MonthlyLimitUsd", "50"); builder.UseSetting("PaidAI:BackgroundLimitUsd", "15");
            builder.UseSetting("PaidAI:InputUsdPerMillion", "3"); builder.UseSetting("PaidAI:OutputUsdPerMillion", "15");
            builder.UseSetting("PaidAI:RateCardModel", "test-model");
            builder.UseSetting("PaidAI:RateCardDate", DateTime.UtcNow.ToString("yyyy-MM-dd"));
            builder.ConfigureServices(s => s.AddHttpClient("providers").ConfigurePrimaryHttpMessageHandler(() => new StubHttpHandler(handle)));
        }
    }
}
