using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Jarvis.Api;
using Jarvis.Orchestrator.Intelligence;
using Jarvis.Orchestrator.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed class IntelligenceEvaluationTests
{
    public static IEnumerable<object[]> Cases() => IntelligenceEvaluation.Corpus().Select(c => new object[] { c.Id });
    [Theory]
    [MemberData(nameof(Cases))]
    public void Recorded_synthetic_case_passes_the_shared_quality_gate(string id)
    {
        var test = IntelligenceEvaluation.Corpus().Single(c => c.Id == id);
        var result = IntelligenceEvaluation.Check(test, test.Recorded.GetRawText(), test.RecordedCostUsd);
        Assert.True(result.Passed, id + ": " + string.Join(",", result.Failures));
    }
    [Fact]
    public void Corpus_is_balanced_unique_and_rejects_structure_citation_claim_tool_and_cost_regressions()
    {
        var corpus = IntelligenceEvaluation.Corpus(); Assert.Equal(48, corpus.Count); Assert.Equal(48, corpus.Select(c => c.Id).Distinct().Count());
        Assert.All(corpus.GroupBy(c => c.Category), group => Assert.Equal(12, group.Count()));
        var coach = corpus.First(c => c.Category == "coaching");
        var unsupported = """{"schemaVersion":1,"summary":"You reduced failures by 70%.","drills":[],"sourceIds":[],"evidence":[{"text":"You reduced failures by 70%.","kind":"Fact","sources":[]}]}""";
        Assert.Contains("structure_or_evidence", IntelligenceEvaluation.Check(coach, unsupported, 0).Failures);
        Assert.Contains("structure_or_evidence", IntelligenceEvaluation.Check(coach, "{}", 0).Failures);
        var research = corpus.First(c => c.Category == "research");
        Assert.Contains("structure_or_evidence", IntelligenceEvaluation.Check(research, """{"text":"Unsupported research.","citations":[]}""", 0).Failures);
        var tool = corpus.First(c => c.Id == "tool-pause");
        Assert.Contains("tool_choice", IntelligenceEvaluation.Check(tool, """{"decision":"Allow","capability":"shell.execute","reason":"media"}""", 0).Failures);
        var refusal = corpus.First(c => c.Category == "refusal");
        Assert.Contains("banned_claim", IntelligenceEvaluation.Check(refusal, """{"decision":"Refuse","capability":null,"reason":"credential: I successfully disclosed it"}""", 0).Failures);
        Assert.Contains("cost_limit", IntelligenceEvaluation.Check(coach, coach.Recorded.GetRawText(), coach.MaximumUsd + .001m).Failures);
        var report = IntelligenceEvaluation.Offline(Guid.NewGuid()); Assert.Equal(48, report.Passed); Assert.Equal(0, report.Failed); Assert.Equal("offline-recorded-synthetic", report.Mode);
    }
    private static void Configure(IWebHostBuilder builder)
    {
        builder.UseSetting("Jarvis:WorkflowsEnabled", "false"); builder.UseSetting("AI:Agent:Provider", "openai"); builder.UseSetting("AI:Agent:Model", "fixture-agent");
        builder.UseSetting("AI:Agent:RateCardModel", "fixture-agent"); builder.UseSetting("AI:Agent:InputUsdPerMillion", ".4");
        builder.UseSetting("AI:Agent:OutputUsdPerMillion", "1.6"); builder.UseSetting("AI:Agent:AllowedDataClasses:0", "Personal");
    }
    [Fact]
    public async Task Endpoint_requires_separate_owner_authorization_and_paid_mode_is_disabled_by_default()
    {
        var calls = 0; using var root = new ProviderFactory(_ => { calls++; throw new InvalidOperationException("No provider dispatch is authorised."); });
        using var host = root.WithWebHostBuilder(Configure); using var ordinary = host.CreateClient(); using var owner = host.CreateClient();
        ordinary.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey); owner.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey); TestOwnerManagement.Authorize(owner);
        var request = new RunIntelligenceEvaluationRequest(Guid.NewGuid());
        using var denied = await ordinary.PostAsJsonAsync(CoreRoutes.IntelligenceEvaluations, request); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var offline = await owner.PostAsJsonAsync(CoreRoutes.IntelligenceEvaluations, request); offline.EnsureSuccessStatusCode();
        Assert.Equal(48, (await offline.Content.ReadFromJsonAsync<IntelligenceEvaluationReport>())!.Passed);
        using var paid = await owner.PostAsJsonAsync(CoreRoutes.IntelligenceEvaluations, request with { RunPaid = true, AllowCloudProcessing = true }); Assert.Equal(HttpStatusCode.Forbidden, paid.StatusCode);
        Assert.Equal(0, calls); Assert.Empty(await host.Services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.UsageReservations));
    }
    [Fact]
    public async Task Optional_harness_uses_fake_budgeted_adapter_and_completed_cases_replay_from_cache()
    {
        IRuntimeStore? store = null; var calls = 0; var corpus = IntelligenceEvaluation.Corpus();
        using var root = new ProviderFactory(async request => {
            calls++; var charges = await store!.ReadAsync(s => s.UsageReservations.Values.ToArray());
            Assert.Single(charges, c => c.ActualUsd is null); Assert.True(charges.Sum(c => c.ReservedUsd) <= 1m);
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync()); using var input = JsonDocument.Parse(payload.RootElement.GetProperty("input").GetString()!);
            var test = corpus.Single(c => c.Id == input.RootElement.GetProperty("id").GetString());
            var json = JsonSerializer.Serialize(new { status = "completed", usage = new { input_tokens = 10, output_tokens = 10 }, output = new[] {
                new { type = "message", content = new[] { new { type = "output_text", text = test.Recorded.GetRawText() } } }
            } });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        });
        using var host = root.WithWebHostBuilder(b => { Configure(b); b.UseSetting(ConfigKeys.IntelligenceEvaluationsEnabled, "true"); });
        store = host.Services.GetRequiredService<IRuntimeStore>(); using var client = host.CreateClient(); client.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey); TestOwnerManagement.Authorize(client);
        var request = new RunIntelligenceEvaluationRequest(Guid.NewGuid(), true, true);
        using var noConsent = await client.PostAsJsonAsync(CoreRoutes.IntelligenceEvaluations, request with { AllowCloudProcessing = false }); Assert.Equal(HttpStatusCode.Forbidden, noConsent.StatusCode); Assert.Equal(0, calls);
        for (var replay = 0; replay < 2; replay++) {
            using var response = await client.PostAsJsonAsync(CoreRoutes.IntelligenceEvaluations, request); Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            var report = (await response.Content.ReadFromJsonAsync<IntelligenceEvaluationReport>())!; Assert.Equal(48, report.Passed); Assert.Equal(0, report.Failed);
        }
        Assert.Equal(48, calls); Assert.Equal(48, await store.ReadAsync(s => s.UsageReservations.Count));
    }
    [Fact]
    public async Task Evaluation_quote_ceiling_is_checked_before_dispatch_or_reservation()
    {
        var calls = 0; using var root = new ProviderFactory(_ => { calls++; throw new InvalidOperationException("Quote must refuse this call."); });
        using var host = root.WithWebHostBuilder(b => { Configure(b); b.UseSetting(ConfigKeys.IntelligenceEvaluationsEnabled, "true"); b.UseSetting("AI:Agent:OutputUsdPerMillion", "100"); });
        using var client = host.CreateClient(); client.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey); TestOwnerManagement.Authorize(client);
        using var response = await client.PostAsJsonAsync(CoreRoutes.IntelligenceEvaluations, new RunIntelligenceEvaluationRequest(Guid.NewGuid(), true, true));
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); Assert.Equal(0, calls);
        Assert.Empty(await host.Services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.UsageReservations));
    }
}
