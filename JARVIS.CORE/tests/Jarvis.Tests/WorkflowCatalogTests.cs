using Jarvis.Contracts;
using Jarvis.Orchestrator.Domains;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using System.Net.Http.Json;
using System.Text.Json;

namespace Jarvis.Tests;

public sealed class WorkflowCatalogTests
{
    [Fact]
    public async Task Registered_extension_is_discoverable_without_exposing_its_domain_context()
    {
        using var root = new JarvisApiFactory();
        using var host = root.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton<IWorkflowCatalog>(new WorkflowCatalog([new("education-practice", [AgentRole.TechnicalTrainer], false, DomainContext: "Private domain reference")]))));
        using var client = host.CreateClient(); client.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey);
        var catalog = await client.GetStringAsync("/api/agents/catalog");
        Assert.Contains("education-practice", catalog); Assert.DoesNotContain("Private domain reference", catalog);
        using var result = await client.PostAsJsonAsync("/api/agents/runs", new StartAgentRunRequest(Guid.NewGuid(), "education-practice", false));
        result.EnsureSuccessStatusCode();
        using var parsed = JsonDocument.Parse(await result.Content.ReadAsStringAsync());
        Assert.Equal("education-practice", parsed.RootElement.GetProperty("run").GetProperty("workflow").GetString());
    }

    [Fact]
    public void Retention_removes_pinned_domain_text_from_completed_runs()
    {
        var state = new RuntimeState(); var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        state.AgentRuns[id] = new(id, null, "fixture", "education-practice", 1, false, now.AddDays(-60), now.AddDays(-59), "Succeeded", [],
            WorkflowPolicy: new("fixture-version", false, false, true, "Private domain reference"));
        var policy = new RetentionPolicy(true);
        var selection = PrivacyData.Select(state, [], [], policy, now);
        PrivacyData.RetainRuntime(state, policy, now, selection);
        Assert.Null(state.AgentRuns[id].WorkflowPolicy!.DomainContext);
        Assert.NotNull(state.AgentRuns[id].DeletedAt);
    }
    [Fact]
    public async Task Extensions_run_on_existing_engine_and_pin_policy_across_catalogue_changes()
    {
        var roles = new List<AgentRole> { AgentRole.TechnicalTrainer, AgentRole.Review };
        var catalog = new WorkflowCatalog([
            new("education-practice", roles, false, false, true, "Education reference"),
            new("language-conversation", [AgentRole.CommunicationCoach], false, false, true)
        ]);
        roles.Clear(); // A module retaining its original collection cannot mutate registered policy.
        var store = new InMemoryRuntimeStore(); var runtime = new AgentRuntime(store, TimeProvider.System, catalog);
        var education = await runtime.StartAsync(new(Guid.NewGuid(), "education-practice", false), default);
        Assert.Equal(2, education.Tasks.Count);
        Assert.Equal("Education reference", education.Run.WorkflowPolicy!.DomainContext);
        Assert.False(education.Run.WorkflowPolicy.UsesPersonalContext);
        Assert.True(catalog.Remove("education-practice"));
        catalog.Register(new("education-practice", [AgentRole.Research], true));
        Assert.NotEqual(education.Run.WorkflowPolicy.Revision, WorkflowCatalog.Snapshot(catalog.Get("education-practice")).Revision);
        var first = (await runtime.ClaimAsync(default))!;
        Assert.Equal(AgentRole.TechnicalTrainer, first.Role);
        await runtime.CompleteAsync(first.Id, first.ClaimToken!.Value, "draft", null, false, default);
        var review = (await runtime.ClaimAsync(default))!;
        Assert.Equal(AgentRole.Review, review.Role);
        await runtime.CompleteAsync(review.Id, review.ClaimToken!.Value, "review", null, false, default);
        await runtime.TickAsync(default);
        Assert.Equal("Succeeded", await store.ReadAsync(s => s.AgentRuns[education.Run.Id].Status));
        var language = await runtime.StartAsync(new(Guid.NewGuid(), "language-conversation", false), default);
        Assert.Single(language.Tasks);
        await Assert.ThrowsAsync<DomainException>(() => runtime.StartAsync(new(Guid.NewGuid(), "education-practice", false), default));
        Assert.False(catalog.Remove("daily-coaching"));
        await runtime.CancelAsync(education.Run.Id, true, default);
        Assert.Null(await store.ReadAsync(s => s.AgentRuns[education.Run.Id].WorkflowPolicy!.DomainContext));
    }

    [Fact]
    public async Task Disabled_extension_does_not_break_other_scheduled_work()
    {
        var catalog = new WorkflowCatalog([new("education-practice", [AgentRole.TechnicalTrainer], false)]);
        var store = new InMemoryRuntimeStore(); var now = TimeProvider.System.GetUtcNow();
        var runtime = new AgentRuntime(store, TimeProvider.System, catalog);
        await runtime.SaveScheduleAsync(new(Guid.NewGuid(), null, "Education", "education-practice", "UTC", now.ToString("HH:mm"), true, false), default);
        catalog.Remove("education-practice");
        Assert.Equal(0, await runtime.TickAsync(default));
        Assert.Empty(await store.ReadAsync(s => s.AgentRuns));
    }
}
