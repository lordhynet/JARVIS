using Jarvis.Api.Integrations;
using Jarvis.Api.Voice;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Domains;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Hosting;
using System.Text.Json;

namespace Jarvis.Tests;

public sealed class DomainCatalogTests
{
    [Fact]
    public async Task Every_existing_learning_and_activity_enum_resolves_to_a_seeded_registry_domain()
    {
        using var root = new JarvisApiFactory(); using var host = root.WithWebHostBuilder(b => b.UseSetting("Jarvis:WorkflowsEnabled", "false"));
        var catalog = host.Services.GetRequiredService<IDomainCatalog>(); var registry = host.Services.GetRequiredService<IDomainRegistry>();
        Assert.Equal("languages", catalog.ForLearning(LearningDomain.French).Key);
        Assert.Equal("payments-fintech", catalog.ForLearning(LearningDomain.PaymentsFintech).Key);
        Assert.Equal("interview-coaching", catalog.ForLearning(LearningDomain.InterviewStory).Key);
        foreach (var domain in Enum.GetValues<LearningDomain>()) Assert.Equal(catalog.ForLearning(domain).Key, (await registry.ResolveLearningAsync(domain, default))!.Definition.Key);
        foreach (var activity in Enum.GetValues<ActivityType>()) Assert.Equal(catalog.ForActivity(activity).Key, (await registry.ResolveActivityAsync(activity, default))!.Definition.Key);
        Assert.Throws<DomainException>(() => catalog.ForLearning((LearningDomain)999));
    }

    [Fact]
    public async Task Adding_missing_installed_seeds_preserves_existing_versions_disabled_state_and_preferences()
    {
        using var root = new JarvisApiFactory(); using var host = root.WithWebHostBuilder(b => b.UseSetting("Jarvis:WorkflowsEnabled", "false"));
        var services = host.Services; var registry = services.GetRequiredService<IDomainRegistry>(); await registry.ListAsync(default);
        var runtime = services.GetRequiredService<IRuntimeStore>();
        var original = await runtime.UpdateAsync(s => {
            var state = s.DomainRegistry;
            var legacyKeys = new[] { "general", "algorithms", "system-design", "interview-coaching", "languages", "social-intelligence", "daily-planning", "reflection" };
            var extra = state.Definitions.Values.Where(d => !legacyKeys.Contains(d.Key)).Select(d => d.Id).ToHashSet();
            var versions = state.Versions.Values.Where(v => extra.Contains(v.DomainId)).Select(v => v.Id).ToHashSet();
            foreach (var id in extra) state.Definitions.Remove(id);
            foreach (var id in versions) state.Versions.Remove(id);
            state.Bindings.RemoveAll(b => versions.Contains(b.DomainVersionId));
            foreach (var id in state.AuditEvents.Values.Where(a => extra.Contains(a.DomainId)).Select(a => a.Id).ToArray()) state.AuditEvents.Remove(id);
            var language = state.Definitions.Values.Single(d => d.Key == "languages");
            state.Definitions[language.Id] = language = language with { Status = DomainStatus.Disabled, Revision = 9 };
            state.UserStates[DomainPolicy.OwnerSubject + ":" + language.Id] = new(DomainPolicy.OwnerSubject, language.Id, false, new("fr-FR", 25), null, 4, DateTimeOffset.UtcNow);
            return (language, state.Versions[language.LatestVersionId], state.UserStates[DomainPolicy.OwnerSubject + ":" + language.Id]);
        });
        IDomainRegistry restarted = new DomainRegistryService(services.GetRequiredService<IDomainRegistryStore>(), services.GetRequiredService<ICapabilityCatalog>(), services.GetRequiredService<TimeProvider>());
        var all = await restarted.ListAsync(default); Assert.Equal(18, all.Count);
        Assert.Equal(4, all.Count(d => d.Definition.Key.StartsWith("scenario-", StringComparison.Ordinal)));
        Assert.Equal(original.language, all.Single(d => d.Definition.Key == "languages").Definition);
        Assert.Equal(JsonSerializer.Serialize(original.Item2, DomainPolicy.Json), JsonSerializer.Serialize(all.Single(d => d.Definition.Key == "languages").Version, DomainPolicy.Json));
        Assert.Equal(original.Item3, await runtime.ReadAsync(s => s.DomainRegistry.UserStates[DomainPolicy.OwnerSubject + ":" + original.language.Id]));
        Assert.Null(await restarted.ResolveLearningAsync(LearningDomain.French, default));
    }
    [Fact]
    public void Installed_domains_preserve_legacy_activity_prompts_and_enums()
    {
        var catalog = new DomainCatalog(new CapabilityCatalog(CoreVoiceTools.Capabilities), new WorkflowCatalog());
        Assert.Equal("Practice French at the user's level. Offer a short correction and one follow-up question.", catalog.ForActivity(ActivityType.French).Instructions);
        Assert.Equal("Help the user with their task and ask a focused question when needed.", OpenAiProvider.ActivityInstructions((ActivityType)999));
        foreach (var activity in Enum.GetValues<ActivityType>()) {
            Assert.Equal(OpenAiProvider.ActivityInstructions(activity), catalog.ForActivity(activity).Instructions);
            Assert.Equal(activity, System.Text.Json.JsonSerializer.Deserialize<ActivityType>(((int)activity).ToString()));
        }
        Assert.Equal(CapabilityRisk.ReadOnly, new CapabilityCatalog(CoreVoiceTools.Capabilities).Get("core_story_search").Risk);
        Assert.Contains("interview-brief", catalog.Get("interview-coaching").Workflows);
    }
    [Fact]
    public void Modules_can_extend_domains_but_cannot_invent_capabilities_or_override_activity_aliases()
    {
        var capabilities = new CapabilityCatalog(CoreVoiceTools.Capabilities); var workflows = new WorkflowCatalog([
            new("education-practice", [AgentRole.TechnicalTrainer], false), new("language-conversation", [AgentRole.CommunicationCoach], false)
        ]);
        var required = new List<string> { "core_context" };
        var catalog = new DomainCatalog(capabilities, workflows, [
            new("education", "Education module instructions", [], ["education-practice"], required),
            new("conversation", "Conversation module instructions", [], ["language-conversation"], ["core_recall"])
        ]);
        required.Clear(); Assert.Single(catalog.Get("education").Capabilities);
        Assert.Contains("conversation", catalog.Keys);
        Assert.Throws<DomainException>(() => new DomainCatalog(capabilities, workflows, [new("law", "Legal study", [], [], ["uninstalled_legal_sources"])]));
        Assert.Throws<DomainException>(() => new DomainCatalog(capabilities, workflows, [new("override", "Override", [ActivityType.French], [], [])]));
    }
    [Fact]
    public async Task Legacy_brief_and_plan_use_catalogue_descriptors_without_joining_agent_task_routes()
    {
        var catalog = new WorkflowCatalog(); var service = new WorkflowService(new InMemoryWorkflowStore(), TimeProvider.System, catalog);
        var brief = await service.CreateBriefAsync(new(Guid.NewGuid(), true)); var plan = await service.CreateAsync(new(Guid.NewGuid()));
        Assert.Equal(catalog.GetLegacy("interview-brief").Version, brief.WorkflowVersion);
        Assert.Equal(catalog.GetLegacy("practice-plan").Key, plan.Kind);
        Assert.DoesNotContain("interview-brief", catalog.Keys);
        Assert.True(catalog.GetLegacy(brief.Kind).RequiresCloud);
        await Assert.ThrowsAsync<DomainException>(() => service.CreateBriefAsync(new(Guid.NewGuid(), false)));
    }
}
