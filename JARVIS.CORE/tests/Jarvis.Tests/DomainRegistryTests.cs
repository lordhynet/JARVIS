using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jarvis.Api.Integrations;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Domains;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;

namespace Jarvis.Tests;

public sealed class DomainRegistryTests
{
    internal static DomainManifest Education(string key = "education") => new(1, key, key == "education" ? "Education" : key,
        "Practice explaining a school topic.", [], [AgentRole.TechnicalTrainer], [new("core_template_practice")],
        ["Explain the topic in your own words.", "Give a worked example.", "Name one question to revisit."], DomainExecutionMode.Template);
    internal static SaveDomainDraftRequest Draft(DomainManifest manifest, int? revision = null) => new(Guid.NewGuid(), revision, JsonSerializer.SerializeToElement(manifest, DomainPolicy.Json));
    internal static async Task<T> Post<T>(HttpClient client, string path, object request) {
        using var response = await client.PostAsJsonAsync(path, request, DomainPolicy.Json);
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>(DomainPolicy.Json))!;
    }
    internal static Task<DomainOperationReceipt> Activate(HttpClient client, DomainPreview preview) => Post<DomainOperationReceipt>(client,
        $"/api/domains/{preview.Definition.Key}/activate", new ActivateDomainRequest(Guid.NewGuid(), preview.Definition.Revision, preview.Version.Id, preview.Version.ManifestHash));

    [Fact]
    public async Task Config_only_domain_requires_owner_then_runs_authored_template_without_paid_reasoning()
    {
        using var host = new JarvisApiFactory().WithWebHostBuilder(b => b.UseSetting("Jarvis:WorkflowsEnabled", "false")); using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey); TestOwnerManagement.Authorize(client);
        var draft = await Post<DomainPreview>(client, "/api/domains/drafts", Draft(Education()));
        Assert.Equal(DomainStatus.PendingApproval, draft.Definition.Status); Assert.Empty(draft.UnmetDependencies);
        using (var ordinary = host.CreateClient()) {
            ordinary.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey);
            using var denied = await ordinary.PostAsJsonAsync("/api/domains/education/activate", new ActivateDomainRequest(Guid.NewGuid(), 1, draft.Version.Id, draft.Version.ManifestHash));
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }
        await Activate(client, draft);
        var catalog = host.Services.GetRequiredService<IWorkflowCatalog>();
        var descriptor = await catalog.ResolveAsync("domain-education-practice", default); Assert.True(descriptor.Deterministic);
        var runtime = host.Services.GetRequiredService<AgentRuntime>();
        var run = await runtime.StartAsync(new(Guid.NewGuid(), descriptor.Key, false), default);
        var task = (await runtime.ClaimAsync(default))!;
        var output = await host.Services.GetRequiredService<IAgentExecutor>().ExecuteAsync(task, default);
        Assert.Contains("authored-template", output); Assert.Contains("worked example", output); Assert.DoesNotContain("Practise one due question", output);
        Assert.Equal(draft.Version.Id, run.Run.WorkflowPolicy!.DomainVersionId);
        Assert.Empty(await host.Services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.UsageReservations.Values.ToArray()));
        await runtime.CompleteAsync(task.Id, task.ClaimToken!.Value, output, null, false, default);
    }

    [Fact]
    public async Task Versions_pin_runs_and_disable_blocks_future_execution_and_rollback_is_explicit()
    {
        using var host = new JarvisApiFactory().WithWebHostBuilder(b => b.UseSetting("Jarvis:WorkflowsEnabled", "false")); using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey); TestOwnerManagement.Authorize(client);
        var first = await Post<DomainPreview>(client, "/api/domains/drafts", Draft(Education()));
        var receipt = await Activate(client, first);
        var runtime = host.Services.GetRequiredService<AgentRuntime>();
        var run = await runtime.StartAsync(new(Guid.NewGuid(), first.Definition.WorkflowKey, false), default);
        var second = await Post<DomainPreview>(client, "/api/domains/drafts", Draft(Education() with { PracticePrompts = ["Revised prompt"] }, receipt.Revision));
        var active = await Activate(client, second);
        Assert.Equal(first.Version.Id, run.Run.WorkflowPolicy!.DomainVersionId);
        var disabled = await Post<DomainOperationReceipt>(client, "/api/domains/education/disable", new DisableDomainRequest(Guid.NewGuid(), active.Revision));
        await Assert.ThrowsAsync<DomainException>(() => runtime.StartAsync(new(Guid.NewGuid(), first.Definition.WorkflowKey, false), default));
        Assert.Null(await runtime.ClaimAsync(default));
        Assert.Equal(AgentRunStatus.Cancelled, await host.Services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.AgentRuns[run.Run.Id].Status));
        var rollbackRequest = new ActivateDomainRequest(Guid.NewGuid(), disabled.Revision, first.Version.Id, first.Version.ManifestHash);
        var rollback = await Post<DomainOperationReceipt>(client, "/api/domains/education/rollback", rollbackRequest);
        Assert.Equal("rollback", rollback.Action);
        Assert.Equal(rollback, await Post<DomainOperationReceipt>(client, "/api/domains/education/rollback", rollbackRequest));
        Assert.Equal(first.Version.Id, (await host.Services.GetRequiredService<IWorkflowCatalog>().ResolveAsync(first.Definition.WorkflowKey, default)).DomainVersionId);
    }

    [Fact]
    public async Task Approval_can_raise_general_risk_but_cannot_lower_inferred_risk_and_persists_choice()
    {
        using var host = new JarvisApiFactory().WithWebHostBuilder(b => b.UseSetting("Jarvis:WorkflowsEnabled", "false"));
        using var client = host.CreateClient(); client.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey); TestOwnerManagement.Authorize(client);
        var general = await Post<DomainPreview>(client, "/api/domains/drafts", Draft(Education("safe-study")));
        var elevated = new ActivateDomainRequest(Guid.NewGuid(), general.Definition.Revision, general.Version.Id, general.Version.ManifestHash,
            "Owner selected stricter risk", DomainRiskClass.Finance);
        var raisedReceipt = await Post<DomainOperationReceipt>(client, "/api/domains/safe-study/activate", elevated);
        var approvals = await host.Services.GetRequiredService<IDomainRegistryStore>().ReadAsync((s, _) => s.Approvals.Values.ToArray(), default);
        Assert.Equal(DomainRiskClass.Finance, Assert.Single(approvals).RiskClass);
        Assert.Equal(DomainRiskClass.Finance, (await host.Services.GetRequiredService<IDomainRegistry>().ResolveActiveAsync("safe-study", default))!.ApprovedRiskClass);
        var loweredAfterApproval = new ActivateDomainRequest(Guid.NewGuid(), raisedReceipt.Revision, general.Version.Id, general.Version.ManifestHash,
            "Attempt to lower previously raised risk", DomainRiskClass.General);
        using var rejectedRaiseDowngrade = await client.PostAsJsonAsync("/api/domains/safe-study/activate", loweredAfterApproval, DomainPolicy.Json);
        Assert.Equal(HttpStatusCode.Conflict, rejectedRaiseDowngrade.StatusCode);

        var finance = await Post<DomainPreview>(client, "/api/domains/drafts", Draft(Education("finance-study") with { Description = "Study finance and investment fundamentals." }));
        var lower = new ActivateDomainRequest(Guid.NewGuid(), finance.Definition.Revision, finance.Version.Id, finance.Version.ManifestHash,
            "Attempt to lower inferred risk", DomainRiskClass.General);
        using var rejected = await client.PostAsJsonAsync("/api/domains/finance-study/activate", lower, DomainPolicy.Json);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        Assert.Null(await host.Services.GetRequiredService<IDomainRegistry>().ResolveActiveAsync("finance-study", default));
    }

    [Fact]
    public async Task Strict_schema_privileges_dependencies_and_aliases_fail_closed()
    {
        using var host = new JarvisApiFactory().WithWebHostBuilder(b => b.UseSetting("Jarvis:WorkflowsEnabled", "false")); using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey); TestOwnerManagement.Authorize(client);
        var json = JsonSerializer.Serialize(Education(), DomainPolicy.Json).TrimEnd('}') + ",\"permissions\":[\"shell\"]}";
        using var injected = await client.PostAsJsonAsync("/api/domains/drafts", new SaveDomainDraftRequest(Guid.NewGuid(), null, JsonDocument.Parse(json).RootElement));
        Assert.Equal(HttpStatusCode.BadRequest, injected.StatusCode);
        var original = await Post<DomainPreview>(client, "/api/domains/drafts", Draft(Education()));
        using var alias = await client.PostAsJsonAsync("/api/domains/drafts", Draft(Education("school") with { Aliases = ["ＥＤＵＣＡＴＩＯＮ"] }), DomainPolicy.Json);
        Assert.Equal(HttpStatusCode.Conflict, alias.StatusCode);
        var law = await Post<DomainPreview>(client, "/api/domains/drafts", Draft(Education("law") with { Description = "Study law and court decisions." }));
        Assert.Equal(DomainStatus.Draft, law.Definition.Status); Assert.Contains(law.UnmetDependencies, x => x.Contains("Trusted legal sources"));
        using var activation = await client.PostAsJsonAsync("/api/domains/law/activate", new ActivateDomainRequest(Guid.NewGuid(), 1, law.Version.Id, law.Version.ManifestHash));
        Assert.Equal(HttpStatusCode.Conflict, activation.StatusCode);
        var missing = await Post<DomainPreview>(client, "/api/domains/drafts", Draft(Education("robotics") with { Capabilities = [new("uninstalled_robot")] }));
        Assert.NotEmpty(missing.UnmetDependencies); Assert.Null(await host.Services.GetRequiredService<IDomainRegistry>().ResolveActiveAsync("robotics", default));
    }

    [Fact]
    public async Task Scheduled_domains_refresh_without_redeploy_and_parent_disable_invalidates_children()
    {
        using var host = new JarvisApiFactory().WithWebHostBuilder(b => b.UseSetting("Jarvis:WorkflowsEnabled", "false"));
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey); TestOwnerManagement.Authorize(client);
        var parent = await Post<DomainPreview>(client, "/api/domains/drafts", Draft(Education()));
        var parentReceipt = await Activate(client, parent);
        var child = await Post<DomainPreview>(client, "/api/domains/drafts", Draft(Education("maths") with { ParentKey = "education" }));
        await Activate(client, child);
        var runtime = host.Services.GetRequiredService<AgentRuntime>();
        var now = DateTimeOffset.UtcNow;
        await runtime.SaveGoalAsync(new(Guid.NewGuid(), null, "Maths", "Practise a worked example", child.Definition.WorkflowKey, false, now.AddSeconds(-1), now.AddDays(1)), default);
        Assert.Equal(1, await runtime.TickAsync(default));
        var run = await host.Services.GetRequiredService<IRuntimeStore>().ReadAsync(s => Assert.Single(s.AgentRuns.Values));
        Assert.Equal(child.Version.Id, run.WorkflowPolicy!.DomainVersionId);
        await Post<DomainOperationReceipt>(client, "/api/domains/education/disable", new DisableDomainRequest(Guid.NewGuid(), parentReceipt.Revision));
        Assert.DoesNotContain(await host.Services.GetRequiredService<IWorkflowCatalog>().ListAsync(default), d => d.DomainKey == "maths");
        using var scope = CoreInvocationContext.Enter(InvocationOrigin.Agent);
        await Assert.ThrowsAsync<DomainException>(() => host.Services.GetRequiredService<IDomainRegistry>().ActivateAsync("maths",
            new(Guid.NewGuid(), 2, child.Version.Id, child.Version.ManifestHash), new("owner", ["domain.manage"]), default));
    }

    [Fact]
    public async Task State_is_namespaced_and_unknown_or_other_domain_execution_cannot_read_it()
    {
        using var host = new JarvisApiFactory().WithWebHostBuilder(b => b.UseSetting("Jarvis:WorkflowsEnabled", "false")); using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey); TestOwnerManagement.Authorize(client);
        await Post<DomainPreview>(client, "/api/domains/drafts", Draft(Education()));
        await Post<DomainPreview>(client, "/api/domains/drafts", Draft(Education("music")));
        var registry = host.Services.GetRequiredService<IDomainRegistry>();
        await Assert.ThrowsAsync<DomainException>(() => registry.GetStateAsync("education", default));
        using (DomainExecutionContext.Enter("music")) await Assert.ThrowsAsync<DomainException>(() => registry.GetStateAsync("education", default));
        using (DomainExecutionContext.Enter("education")) {
            var state = await registry.SaveStateAsync("education", new(0, true, new("fr", 20)), default);
            Assert.Equal("fr", state.Preferences.Language);
            await Assert.ThrowsAsync<DomainException>(() => registry.SaveStateAsync("education", new(0, false, new()), default));
        }
        using (DomainExecutionContext.Enter("music")) Assert.Equal("en-GB", (await registry.GetStateAsync("music", default)).Preferences.Language);
    }
}

