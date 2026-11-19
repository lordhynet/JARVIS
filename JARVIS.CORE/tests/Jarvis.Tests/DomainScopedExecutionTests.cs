using System.Net;
using System.Text;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Domains;
using Jarvis.Orchestrator.Intelligence;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed class DomainScopedExecutionTests
{
    private static HttpResponseMessage Reply(string text) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
        status = "completed", usage = new { input_tokens = 10, output_tokens = 10 }, output = new[] {
            new { type = "message", content = new[] { new { type = "output_text", text } } }
        }
    }), Encoding.UTF8, "application/json") };
    private static string Artifact => JsonSerializer.Serialize(new { schemaVersion = 1, summary = "Suggested practice", drills = Array.Empty<string>(), sourceIds = Array.Empty<Guid>(),
        evidence = new[] { new { text = "Suggested practice", kind = "Suggestion", sources = Array.Empty<Guid>() } } });
    private static void Configure(IWebHostBuilder b)
    {
        b.UseSetting("Jarvis:WorkflowsEnabled", "false"); b.UseSetting("AI:Agent:Provider", "openai"); b.UseSetting("AI:Agent:Model", "fixture-agent");
        b.UseSetting("AI:Agent:RateCardModel", "fixture-agent"); b.UseSetting("AI:Agent:InputUsdPerMillion", ".4"); b.UseSetting("AI:Agent:OutputUsdPerMillion", "1.6");
        b.UseSetting("AI:Agent:AllowedDataClasses:0", "Personal");
    }
    [Fact]
    public async Task Unsupported_claims_are_refused_after_fake_provider_io_and_never_become_task_artifacts()
    {
        using var root = new ProviderFactory(_ => Task.FromResult(Reply(JsonSerializer.Serialize(new {
            schemaVersion = 1, summary = "You reduced failures by 70%.", drills = Array.Empty<string>(), sourceIds = Array.Empty<Guid>(),
            evidence = new[] { new { text = "You reduced failures by 70%.", kind = "Fact", sources = Array.Empty<Guid>() } }
        }))));
        using var host = root.WithWebHostBuilder(Configure); var services = host.Services;
        var runtime = services.GetRequiredService<AgentRuntime>(); await runtime.StartAsync(new(Guid.NewGuid(), "daily-coaching", true, "Give grounded feedback"), default);
        var task = (await runtime.ClaimAsync(default))!;
        var rejected = await Assert.ThrowsAsync<DomainException>(() => services.GetRequiredService<IAgentExecutor>().ExecuteAsync(task, default));
        Assert.Equal(502, rejected.StatusCode);
        var store = services.GetRequiredService<IRuntimeStore>(); Assert.Null(await store.ReadAsync(s => s.AgentTasks[task.Id].Output));
        Assert.NotNull(Assert.Single(await store.ReadAsync(s => s.UsageReservations.Values.ToArray())).ActualUsd);
    }
    [Fact]
    public async Task Factory_uses_the_existing_budget_adapter_and_never_dispatches_without_a_reservation()
    {
        IRuntimeStore? store = null; var calls = 0;
        using var root = new ProviderFactory(async _ => { calls++; Assert.Null(Assert.Single(await store!.ReadAsync(s => s.UsageReservations.Values.ToArray())).ActualUsd); return Reply(DomainPolicy.Serialize(DomainRegistryTests.Education())); });
        using var host = root.WithWebHostBuilder(Configure); store = host.Services.GetRequiredService<IRuntimeStore>();
        using var manual = CoreInvocationContext.Enter(InvocationOrigin.ManualClient);
        var request = new ProposeDomainRequest(Guid.NewGuid(), "Create Education to study Spanish", true);
        var factory = host.Services.GetRequiredService<IDomainFactory>(); var first = await factory.ProposeAsync(request, default);
        var replay = await factory.ProposeAsync(request, default); Assert.Equal(first.Preview.Version.Id, replay.Preview.Version.Id); Assert.Equal(1, calls);
        Assert.NotNull(Assert.Single(await store.ReadAsync(s => s.UsageReservations.Values.ToArray())).ActualUsd);
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Assisted_domains_use_only_current_consented_general_context_and_disable_fences_publication(bool sensitiveDomain, bool disableDuringCall)
    {
        IRuntimeStore? store = null; var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var root = new ProviderFactory(async request => {
            Assert.Null(Assert.Single(await store!.ReadAsync(s => s.UsageReservations.Values.ToArray())).ActualUsd);
            var body = await request.Content!.ReadAsStringAsync();
            Assert.Equal(!sensitiveDomain, body.Contains("Practise irregular verbs", StringComparison.Ordinal));
            Assert.Equal(!sensitiveDomain, body.Contains("Owner imported excerpt", StringComparison.Ordinal));
            Assert.DoesNotContain("Sensitive local note", body); Assert.DoesNotContain("Other domain facts", body);
            started.SetResult(); if (disableDuringCall) await release.Task; return Reply(Artifact);
        });
        using var host = root.WithWebHostBuilder(Configure); var services = host.Services; store = services.GetRequiredService<IRuntimeStore>();
        using var manual = CoreInvocationContext.Enter(InvocationOrigin.ManualClient);
        var registry = services.GetRequiredService<IDomainRegistry>();
        var document = await services.GetRequiredService<ImportService>().ImportAsync(new(Guid.NewGuid(), "cv", "Owner reference", "Owner imported excerpt", true));
        var manifest = DomainRegistryTests.Education() with { ExecutionMode = DomainExecutionMode.Assisted, Capabilities = [new(AgentCapabilityIds.Reasoning)], KnowledgeSources = [new("import", document.Id.ToString())] };
        var preview = await registry.SaveDraftAsync(DomainRegistryTests.Draft(manifest), "owner", default);
        var approved = await registry.ActivateAsync("education", new(Guid.NewGuid(), preview.Definition.Revision, preview.Version.Id, preview.Version.ManifestHash, RiskClass: sensitiveDomain ? DomainRiskClass.Finance : DomainRiskClass.General), new("owner", ["domain.manage"]), default);
        var memory = services.GetRequiredService<ContextMemoryService>(); var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        await memory.SaveAsync("education", new(Guid.NewGuid(), "note", null, "Practise irregular verbs", now.AddDays(5)));
        await memory.SaveAsync("education", new(Guid.NewGuid(), "note", null, "Sensitive local note", now.AddDays(5), Sensitivity: MemorySensitivity.Sensitive));
        await memory.SaveAsync("general", new(Guid.NewGuid(), "note", null, "Other domain facts", now.AddDays(5)));
        var runtime = services.GetRequiredService<AgentRuntime>(); var run = await runtime.StartAsync(new(Guid.NewGuid(), preview.Definition.WorkflowKey, true, "Spanish verbs"), default);
        var task = (await runtime.ClaimAsync(default))!; var pending = services.GetRequiredService<IAgentExecutor>().ExecuteAsync(task, default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        if (disableDuringCall) {
            await registry.DisableAsync("education", new(Guid.NewGuid(), approved.Revision), new("owner", ["domain.manage"]), default);
            release.SetResult(); await Assert.ThrowsAsync<DomainException>(() => pending);
            Assert.Equal(AgentRunStatus.Cancelled, await store.ReadAsync(s => s.AgentRuns[run.Run.Id].Status));
            Assert.Empty(await store.ReadAsync(s => s.ProviderResults)); Assert.NotEmpty(await store.ReadAsync(s => s.UsageReservations));
        } else { Assert.Contains("Suggested practice", await pending); }
        Assert.Equal(2, (await memory.ListAsync("education")).Count);
    }
}
