using Jarvis.Api.Integrations;
using Jarvis.Api.Voice;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Domains;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;

namespace Jarvis.Tests;

public sealed class DomainCatalogTests
{
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
