using System.Net.Http.Json;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Domains;
using Jarvis.Orchestrator.Intelligence;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed class ContextMemoryTests
{
    [Fact]
    public async Task Owner_memory_is_scoped_idempotent_expiring_exportable_and_survives_disabling()
    {
        using var host = new JarvisApiFactory().WithWebHostBuilder(b => b.UseSetting("Jarvis:WorkflowsEnabled", "false"));
        var services = host.Services; var memory = services.GetRequiredService<ContextMemoryService>();
        var clock = services.GetRequiredService<TimeProvider>(); var now = clock.GetUtcNow();
        using var manual = CoreInvocationContext.Enter(InvocationOrigin.ManualClient);
        var request = new SaveContextMemoryRequest(Guid.NewGuid(), "note", null, "Practise Spanish irregular verbs", now.AddDays(30));
        var saved = await memory.SaveAsync("languages", request); Assert.Equal(saved, await memory.SaveAsync("languages", request));
        var sensitive = await memory.SaveAsync("languages", new(Guid.NewGuid(), "note", null, "Sensitive verb examples", now.AddDays(30), Sensitivity: MemorySensitivity.Sensitive));
        await memory.SaveAsync("general", new(Guid.NewGuid(), "note", null, "Unrelated private facts", now.AddDays(30)));
        var store = services.GetRequiredService<IRuntimeStore>();
        Assert.Equal(saved.Id, Assert.Single(await store.ReadAsync(s => ContextSelector.SelectMemory(s, "languages", "verbs", now))).Id);
        Assert.DoesNotContain(await store.ReadAsync(s => ContextSelector.SelectMemory(s, "languages", "verbs", now)), m => m.Id == sensitive.Id);
        Assert.Empty(await store.ReadAsync(s => ContextSelector.SelectMemory(s, "languages", "verbs", now.AddDays(31))));
        await Assert.ThrowsAsync<DomainException>(() => memory.SaveAsync("languages", request with { Text = "Changed" }));
        var registry = services.GetRequiredService<IDomainRegistry>(); var preview = await registry.PreviewAsync("languages", null, default);
        await registry.DisableAsync("languages", new(Guid.NewGuid(), preview.Definition.Revision), new("owner", ["domain.manage"]), default);
        Assert.Equal(2, (await memory.ListAsync("languages")).Count);
        await Assert.ThrowsAsync<DomainException>(() => memory.SaveAsync("languages", request with { RequestId = Guid.NewGuid() }));
        var export = await services.GetRequiredService<IPrivacyReader>().ExportAsync(now);
        Assert.Equal(3, export.ContextMemories!.Count); Assert.Empty(PrivacyData.EmptyAfterErase(await store.ReadAsync(s => s)).ContextMemories);
        await memory.DeleteAsync("languages", saved.Id); Assert.Single(await memory.ListAsync("languages"));
    }

    [Fact]
    public async Task Captured_sources_are_canonical_restricted_speech_is_refused_and_source_deletion_cascades()
    {
        using var host = new JarvisApiFactory().WithWebHostBuilder(b => b.UseSetting("Jarvis:WorkflowsEnabled", "false"));
        var services = host.Services; var store = services.GetRequiredService<IRuntimeStore>(); var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        var source = Guid.NewGuid(); var session = Guid.NewGuid();
        await store.UpdateAsync(s => { s.VoiceTurns[source] = new(source, session, ChatRoles.User, "My Spanish goal", null, now, Restricted: true); return true; });
        var memory = services.GetRequiredService<ContextMemoryService>(); using var manual = CoreInvocationContext.Enter(InvocationOrigin.ManualClient);
        var request = new SaveContextMemoryRequest(Guid.NewGuid(), "voice", source, null, now.AddDays(10), Length: 15);
        await Assert.ThrowsAsync<DomainException>(() => memory.SaveAsync("languages", request));
        await store.UpdateAsync(s => { s.VoiceTurns[source] = s.VoiceTurns[source] with { Restricted = false }; return true; });
        var saved = await memory.SaveAsync("languages", request); Assert.Equal("My Spanish goal", saved.Text); Assert.Equal(session, saved.SessionId);
        await Assert.ThrowsAsync<DomainException>(() => memory.SaveAsync("languages", request with { RequestId = Guid.NewGuid(), Text = "replacement" }));
        var document = await services.GetRequiredService<ImportService>().ImportAsync(new(Guid.NewGuid(), "cv", "Saved file", "Source example", true));
        var imported = await memory.SaveAsync("languages", new(Guid.NewGuid(), "import", document.Id, null, now.AddDays(10), Length: 14));
        await services.GetRequiredService<ImportService>().DeleteAsync(document.Id, document.Revision);
        Assert.False(await store.ReadAsync(s => s.ContextMemories.ContainsKey(imported.Id)));
        await store.UpdateAsync(s => { AgentRuntime.InvalidateSource(s, "voice", source); s.VoiceTurns.Remove(source); return true; });
        Assert.Empty(await memory.ListAsync("languages"));
    }

    [Fact]
    public void Retrieval_prefers_relevance_then_freshness_and_respects_utf8_bound()
    {
        var state = new RuntimeState(); var now = DateTimeOffset.UtcNow;
        ContextMemoryItem Add(string text, DateTimeOffset at) { var id = Guid.NewGuid(); var item = new ContextMemoryItem(id, "languages", Guid.NewGuid(), text, new("note", id, PrivacyData.Hash(text)), null, MemorySensitivity.Personal, at, now.AddDays(1)); state.ContextMemories[id] = item; return item; }
        var oldMatch = Add("Spanish verbs", now.AddDays(-4)); var freshMatch = Add("Spanish verbs", now.AddDays(-1)); var unrelated = Add("Python function", now);
        Assert.Equal(new[] { freshMatch.Id, oldMatch.Id, unrelated.Id }, ContextSelector.SelectMemory(state, "languages", "Spanish verbs", now).Select(m => m.Id));
        Add(new string('語', 1200), now); var selected = ContextSelector.SelectMemory(state, "languages", "Spanish verbs", now, 2000);
        Assert.True(selected.Sum(m => System.Text.Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(m))) <= 2000);
    }

    [Fact]
    public async Task Registry_templates_reuse_only_installed_standalone_coaching_exercises()
    {
        using var host = new JarvisApiFactory().WithWebHostBuilder(b => b.UseSetting("Jarvis:WorkflowsEnabled", "false"));
        using var manual = CoreInvocationContext.Enter(InvocationOrigin.ManualClient);
        var manifest = DomainRegistryTests.Education() with { Configuration = new Dictionary<string, string> { ["coachingExerciseId"] = "explain-simply" } };
        var registry = host.Services.GetRequiredService<IDomainRegistry>(); var draft = await registry.SaveDraftAsync(DomainRegistryTests.Draft(manifest), "owner", default);
        await registry.ActivateAsync("education", new(Guid.NewGuid(), draft.Definition.Revision, draft.Version.Id, draft.Version.ManifestHash), new("owner", ["domain.manage"]), default);
        var workflow = await host.Services.GetRequiredService<IWorkflowCatalog>().ResolveAsync(draft.Definition.WorkflowKey, default);
        Assert.Equal(Jarvis.Orchestrator.Coaching.CoachingCatalog.Get("explain-simply").Prompt, workflow.PracticeTemplate!.Prompts[0]);
        Assert.Throws<DomainException>(() => DomainPolicy.Validate(manifest with { Configuration = new Dictionary<string, string> { ["coachingExerciseId"] = "invented-workflow" } }));
        Assert.Throws<DomainException>(() => DomainPolicy.Validate(manifest with { Configuration = new Dictionary<string, string> { ["coachingExerciseId"] = "cv-defence" } }));
    }
    [Fact]
    public void Optional_manifest_fields_preserve_existing_canonical_bytes_and_hash()
    {
        var manifest = DomainRegistryTests.Education(); var serialized = DomainPolicy.Serialize(manifest);
        Assert.DoesNotContain("studyGuidance", serialized); Assert.DoesNotContain("knowledgeSources", serialized);
        Assert.Equal(serialized, DomainPolicy.Serialize(manifest with { StudyGuidance = "  ", KnowledgeSources = [] }));
        Assert.Contains("\"parentKey\":null", serialized);
    }
}
