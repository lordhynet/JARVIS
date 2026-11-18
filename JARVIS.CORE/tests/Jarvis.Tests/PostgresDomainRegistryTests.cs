using System.Net;
using System.Net.Http.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Domains;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task Domain_memory_survives_restart_and_disabled_domains_keep_notes_but_cancel_active_runs()
    {
        await using var database = await TestDatabase.CreateAsync(); Guid memoryId;
        using (var first = new PostgresApiFactory(database.ConnectionString))
        using (var client = first.AuthorisedClient()) {
            var draft = await DomainRegistryTests.Post<DomainPreview>(client, "/api/domains/drafts", DomainRegistryTests.Draft(DomainRegistryTests.Education()));
            var approved = await DomainRegistryTests.Activate(client, draft);
            var item = await DomainRegistryTests.Post<ContextMemoryItem>(client, "/api/domains/education/memory", new SaveContextMemoryRequest(Guid.NewGuid(), "note", null, "Explain Spanish verbs", DateTimeOffset.UtcNow.AddDays(10)));
            memoryId = item.Id;
            var runtime = first.Services.GetRequiredService<Jarvis.Orchestrator.Workflows.AgentRuntime>();
            var run = await runtime.StartAsync(new(Guid.NewGuid(), draft.Definition.WorkflowKey, false), default);
            await DomainRegistryTests.Post<DomainOperationReceipt>(client, "/api/domains/education/disable", new DisableDomainRequest(Guid.NewGuid(), approved.Revision));
            Assert.Equal(AgentRunStatus.Cancelled, await first.Services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.AgentRuns[run.Run.Id].Status));
        }
        using var restarted = new PostgresApiFactory(database.ConnectionString); using var owner = restarted.AuthorisedClient();
        var memory = await owner.GetFromJsonAsync<ContextMemoryItem[]>("/api/domains/education/memory", Json);
        Assert.Equal(memoryId, Assert.Single(memory!).Id);
        var status = await owner.GetFromJsonAsync<PrivacyStatus>("/api/privacy/status", Json);
        await DomainRegistryTests.Post<PrivacyStatus>(owner, "/api/privacy/erase", new EraseCoreRequest(Guid.NewGuid(), status!.Revision, PrivacyData.ErasePhrase));
        Assert.Empty((await restarted.Services.GetRequiredService<IPrivacyReader>().ExportAsync(DateTimeOffset.UtcNow)).ContextMemories!);
    }
    [PostgresFact]
    public async Task Domain_registry_survives_restart_and_is_relational_and_privacy_erasable()
    {
        await using var database = await TestDatabase.CreateAsync(); DomainPreview draft; DomainOperationReceipt approved;
        using (var first = new PostgresApiFactory(database.ConnectionString))
        using (var client = first.AuthorisedClient()) {
            draft = await DomainRegistryTests.Post<DomainPreview>(client, "/api/domains/drafts", DomainRegistryTests.Draft(DomainRegistryTests.Education()));
            approved = await DomainRegistryTests.Activate(client, draft);
            var factory = first.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>();
            await using var db = await factory.CreateDbContextAsync();
            Assert.True(await db.DomainVersions.AnyAsync(v => v.Id == draft.Version.Id));
            var runtime = await first.Services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.DomainRegistry.Definitions.Count);
            Assert.Equal(0, runtime);
        }
        using var restarted = new PostgresApiFactory(database.ConnectionString); using var c = restarted.AuthorisedClient();
        var registry = restarted.Services.GetRequiredService<IDomainRegistry>();
        Assert.Equal(draft.Version.Id, (await registry.ResolveActiveAsync("education", default))!.Version.Id);
        var export = await c.GetFromJsonAsync<CoreDataExport>("/api/privacy/export", Json);
        Assert.Equal(DomainRiskClass.General, Assert.Single(export!.Domains!.Approvals, a => a.RequestId == approved.RequestId).RiskClass);
        var status = await c.GetFromJsonAsync<PrivacyStatus>("/api/privacy/status", Json);
        await DomainRegistryTests.Post<PrivacyStatus>(c, "/api/privacy/erase", new EraseCoreRequest(Guid.NewGuid(), status!.Revision, PrivacyData.ErasePhrase));
        await using var verify = await restarted.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>().CreateDbContextAsync();
        Assert.Empty(await verify.DomainDefinitions.ToArrayAsync()); Assert.Empty(await verify.DomainVersions.ToArrayAsync()); Assert.Empty(await verify.DomainApprovals.ToArrayAsync());
        await Assert.ThrowsAsync<DomainException>(() => registry.ResolveActiveAsync("education", default));
    }

    [PostgresFact]
    public async Task Concurrent_hosts_cannot_create_alias_duplicates_or_reactivate_stale_revisions()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var a = new PostgresApiFactory(database.ConnectionString); using var b = new PostgresApiFactory(database.ConnectionString);
        using var ca = a.AuthorisedClient(); using var cb = b.AuthorisedClient();
        // Ensure both hosts completed installed-catalogue seeding before the race.
        await ca.GetAsync("/api/domains"); await cb.GetAsync("/api/domains");
        var responses = await Task.WhenAll(ca.PostAsJsonAsync("/api/domains/drafts", DomainRegistryTests.Draft(DomainRegistryTests.Education()), DomainPolicy.Json),
            cb.PostAsJsonAsync("/api/domains/drafts", DomainRegistryTests.Draft(DomainRegistryTests.Education("school") with { Aliases = ["Education"] }), DomainPolicy.Json));
        try {
            Assert.Single(responses, r => r.IsSuccessStatusCode); Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
            var saved = (await responses.Single(r => r.IsSuccessStatusCode).Content.ReadFromJsonAsync<DomainPreview>(DomainPolicy.Json))!;
            var request = new ActivateDomainRequest(Guid.NewGuid(), saved.Definition.Revision, saved.Version.Id, saved.Version.ManifestHash);
            var receipts = await Task.WhenAll(DomainRegistryTests.Post<DomainOperationReceipt>(ca, $"/api/domains/{saved.Definition.Key}/activate", request),
                DomainRegistryTests.Post<DomainOperationReceipt>(cb, $"/api/domains/{saved.Definition.Key}/activate", request));
            Assert.Equal(receipts[0], receipts[1]);
            using var stale = await ca.PostAsJsonAsync($"/api/domains/{saved.Definition.Key}/activate", request with { RequestId = Guid.NewGuid() }, DomainPolicy.Json);
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        } finally { foreach (var response in responses) response.Dispose(); }
    }
}
