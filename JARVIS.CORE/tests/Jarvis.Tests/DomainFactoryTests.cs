using System.Net.Http.Json;
using System.Text.Json;
using Jarvis.Api.Voice;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Domains;
using Jarvis.Orchestrator.Intelligence;
using Jarvis.Orchestrator.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed class DomainFactoryTests
{
    private sealed class Generator : ITextReasoningProvider
    {
        public int Calls;
        public string Output = DomainPolicy.Serialize(DomainRegistryTests.Education());
        public TextReasoningTask? Last;
        public Task<TextReasoningResult> ReasonAsync(TextReasoningTask task, CancellationToken ct) {
            Calls++; Last = task;
            return Task.FromResult(new TextReasoningResult(Output, [], new("fixture", "fixture", "Agent", "fixture", "fixture", new(2026, 11, 15), 1, 1, 1200)));
        }
    }

    [Theory]
    [InlineData("JARVIS, add a Law domain.")]
    [InlineData("JARVIS, create an Education domain.")]
    [InlineData("JARVIS, configure a Spanish learning programme.")]
    [InlineData("Set up a Spanish learning program")]
    [InlineData("Start a course for interview practice")]
    [InlineData("Configure a subject module for biology")]
    public void Explicit_voice_proposal_intent_accepts_requirement_examples_and_supported_wording(string phrase) =>
        Assert.True(DomainFactory.IsExplicitProposalRequest(phrase));

    [Theory]
    [InlineData("Configure Spanish pronunciation")]
    [InlineData("Start speaking practice")]
    [InlineData("Make me a cup of tea")]
    public void Voice_proposal_intent_rejects_general_requests(string phrase) =>
        Assert.False(DomainFactory.IsExplicitProposalRequest(phrase));
    [Fact]
    public async Task Owner_sources_and_original_guidance_are_bound_by_core_and_invented_references_are_refused()
    {
        var generator = new Generator();
        using var host = new JarvisApiFactory().WithWebHostBuilder(b => { b.UseSetting("Jarvis:WorkflowsEnabled", "false"); b.ConfigureServices(s => s.AddSingleton<ITextReasoningProvider>(generator)); });
        using var manual = CoreInvocationContext.Enter(InvocationOrigin.ManualClient);
        var import = await host.Services.GetRequiredService<ImportService>().ImportAsync(new(Guid.NewGuid(), "cv", "Owner file", "Canonical owner knowledge", true));
        var factory = host.Services.GetRequiredService<IDomainFactory>();
        DomainKnowledgeReference[] sources = [new("url", "https://example.org/spanish"), new("import", import.Id.ToString())];
        var request = new ProposeDomainRequest(Guid.NewGuid(), "Create Education for Spanish verb practice", true, KnowledgeSources: sources);
        var result = await factory.ProposeAsync(request, default);
        Assert.Equal(request.Description, result.Preview.Version.Manifest.StudyGuidance);
        Assert.Equal(DomainPolicy.ValidateKnowledge(sources), result.Preview.Version.Manifest.KnowledgeSources);
        Assert.DoesNotContain("Canonical owner knowledge", DomainPolicy.Serialize(result.Preview.Version.Manifest));
        Assert.Null(result.Preview.Definition.CurrentVersionId);
        generator.Output = DomainPolicy.Serialize(DomainRegistryTests.Education("invented") with { KnowledgeSources = [new("url", "https://example.org/minted")] });
        await Assert.ThrowsAsync<DomainException>(() => factory.ProposeAsync(new(Guid.NewGuid(), "Create something", true), default));
        var calls = generator.Calls;
        await host.Services.GetRequiredService<ImportService>().DeleteAsync(import.Id, import.Revision);
        await Assert.ThrowsAsync<DomainException>(() => factory.ProposeAsync(request with { RequestId = Guid.NewGuid() }, default));
        Assert.Equal(calls, generator.Calls);
    }
    [Theory]
    [InlineData("{\"provider\":\"privileged\"}")]
    [InlineData("{\"dataClasses\":[\"Sensitive\"]}")]
    [InlineData("{\"tools\":[\"shell\"]}")]
    public void Drafts_cannot_mint_provider_data_class_or_tool_fields(string extra)
    {
        var json = DomainPolicy.Serialize(DomainRegistryTests.Education());
        using var document = JsonDocument.Parse(json[..^1] + "," + extra[1..]);
        Assert.Throws<DomainException>(() => DomainPolicy.Parse(document.RootElement));
    }
    [Fact]
    public async Task Generation_saves_a_provenanced_draft_and_replay_does_not_regenerate_or_activate()
    {
        var generator = new Generator();
        using var host = new JarvisApiFactory().WithWebHostBuilder(b => { b.UseSetting("Jarvis:WorkflowsEnabled", "false"); b.ConfigureServices(s => s.AddSingleton<ITextReasoningProvider>(generator)); });
        using var client = host.CreateClient(); client.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey);
        var request = new ProposeDomainRequest(Guid.NewGuid(), "Add an Education domain for explaining maths examples", true);
        var proposal = await DomainRegistryTests.Post<DomainProposal>(client, "/api/domains/proposals", request);
        var retry = await DomainRegistryTests.Post<DomainProposal>(client, "/api/domains/proposals", request);
        Assert.Equal(proposal.Preview.Version.Id, retry.Preview.Version.Id); Assert.Equal(1, generator.Calls);
        Assert.Equal("manual-generation", proposal.Provenance!.Source); Assert.Equal(request.RequestId, proposal.Provenance.GenerationId);
        Assert.Null(proposal.Preview.Definition.CurrentVersionId);
        Assert.Null(await host.Services.GetRequiredService<IDomainRegistry>().ResolveActiveAsync("education", default));
        Assert.Equal(TextCapability.JsonReasoning, generator.Last!.Capability); Assert.Equal(ReasoningDataClass.Personal, generator.Last.DataClass);
        using var changed = await client.PostAsJsonAsync("/api/domains/proposals", request with { Description = "Different" });
        Assert.Equal(System.Net.HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Equal(1, generator.Calls);
    }
    [Fact]
    public async Task Model_cannot_add_code_permissions_or_personal_context()
    {
        var generator = new Generator();
        using var host = new JarvisApiFactory().WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<ITextReasoningProvider>(generator)));
        using var scope = CoreInvocationContext.Enter(InvocationOrigin.ManualClient);
        var factory = host.Services.GetRequiredService<IDomainFactory>();
        generator.Output = generator.Output.TrimEnd('}') + ",\"sql\":\"CREATE TABLE\"}";
        await Assert.ThrowsAsync<DomainException>(() => factory.ProposeAsync(new(Guid.NewGuid(), "Create a domain", true), default));
        generator.Output = DomainPolicy.Serialize(DomainRegistryTests.Education() with { UsesPersonalContext = true });
        await Assert.ThrowsAsync<DomainException>(() => factory.ProposeAsync(new(Guid.NewGuid(), "Create a domain", true), default));
        Assert.DoesNotContain(await host.Services.GetRequiredService<IDomainRegistry>().ListAsync(default), x => x.Definition.Key == "education");
    }
    [Fact]
    public async Task Uninstalled_requested_features_remain_nonactivatable_dependencies_and_no_consent_makes_no_call()
    {
        var generator = new Generator { Output = DomainPolicy.Serialize(DomainRegistryTests.Education() with { Capabilities = [new("uninstalled_quiz_scoring")] }) };
        using var host = new JarvisApiFactory().WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<ITextReasoningProvider>(generator)));
        var factory = host.Services.GetRequiredService<IDomainFactory>(); using var scope = CoreInvocationContext.Enter(InvocationOrigin.ManualClient);
        await Assert.ThrowsAsync<DomainException>(() => factory.ProposeAsync(new(Guid.NewGuid(), "Add Education quizzes", false), default)); Assert.Equal(0, generator.Calls);
        var proposal = await factory.ProposeAsync(new(Guid.NewGuid(), "Add Education quizzes", true), default);
        Assert.Equal(DomainStatus.Draft, proposal.Preview.Definition.Status); Assert.Contains(proposal.Preview.UnmetDependencies, x => x.Contains("uninstalled_quiz_scoring"));
        var reused = await factory.ProposeAsync(new(Guid.NewGuid(), "Enable Education", true, "education"), default);
        Assert.True(reused.ExistingDomain); Assert.Equal(1, generator.Calls);
    }
    [Fact]
    public async Task Voice_can_propose_only_from_a_captured_explicit_request_and_has_no_activation_tool()
    {
        var generator = new Generator();
        using var host = new JarvisApiFactory().WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<ITextReasoningProvider>(generator)));
        var runtime = host.Services.GetRequiredService<IRuntimeStore>(); var session = Guid.NewGuid(); var source = Guid.NewGuid();
        var tools = host.Services.GetRequiredService<CoreVoiceTools>();
        await VoiceToolFixture.OpenAsync(host.Services, session);
        var args = JsonSerializer.Serialize(new { utterance_id = source });
        Assert.Contains("latest user speech", await tools.ExecuteAsync("core_domain_propose", args, session, new HashSet<Guid>(), default)); Assert.Equal(0, generator.Calls);
        await runtime.UpdateAsync(s => { s.VoiceTurns[source] = new(source, session, "user", "Hello there", null, DateTimeOffset.UtcNow); return true; });
        Assert.Contains("explicitly", await tools.ExecuteAsync("core_domain_propose", args, session, new HashSet<Guid> { source }, default)); Assert.Equal(0, generator.Calls);
        await runtime.UpdateAsync(s => { s.VoiceTurns[source] = s.VoiceTurns[source] with { Text = "Add an Education domain for maths practice" }; return true; });
        var output = await tools.ExecuteAsync("core_domain_propose", args, session, new HashSet<Guid> { source }, default);
        Assert.Contains("owner approval", output); Assert.Equal(1, generator.Calls);
        Assert.Contains("Add an Education domain for maths practice", generator.Last!.Messages.Single().Text);
        var preview = await host.Services.GetRequiredService<IDomainRegistry>().PreviewAsync("education", null, default); Assert.Null(preview.Definition.CurrentVersionId);
        Assert.DoesNotContain(CoreVoiceTools.Definitions, t => JsonSerializer.Serialize(t).Contains("core_domain_activate"));
    }
}
