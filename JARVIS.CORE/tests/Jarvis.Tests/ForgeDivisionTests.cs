using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Domains;
using Jarvis.Orchestrator.Forge;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed class ForgeDivisionTests
{
    private const string Drafts = "/api/forge/divisions/drafts";

    [Fact]
    public void Catalog_is_sixteen_safe_template_divisions_with_protected_and_optional_classes()
    {
        Assert.Equal(16, ForgeDivisions.All.Count);
        Assert.Equal(16, ForgeDivisions.All.Select(d => d.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.All(ForgeDivisions.All, d => {
            Assert.StartsWith(ForgeDivisions.KeyPrefix, d.Key, StringComparison.Ordinal);
            Assert.True(DomainPolicy.IsValidKey(d.Key));
            var manifest = ForgeDivisions.Manifest(d); // throws if the existing schema rejects it
            Assert.Equal(DomainExecutionMode.Template, manifest.ExecutionMode);
            Assert.False(manifest.UsesPersonalContext);
            Assert.Equal("core_template_practice", Assert.Single(manifest.Capabilities).Id);
            Assert.InRange(manifest.PracticePrompts.Count, 1, 3);
        });
        Assert.Equal(new[] { "forge.global", "forge.health", "forge.relationships", "forge.wealth" },
            ForgeDivisions.All.Where(d => d.Protected).Select(d => d.Key).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal("forge.relationships", Assert.Single(ForgeDivisions.All, d => d.Optional).Key);
        Assert.Equal(DomainRiskClass.Finance, DomainPolicy.Risk(ForgeDivisions.Manifest(ForgeDivisions.All.Single(d => d.Code == "WEALTH"))));
        Assert.Equal(DomainRiskClass.Health, DomainPolicy.Risk(ForgeDivisions.Manifest(ForgeDivisions.All.Single(d => d.Code == "HEALTH"))));
        Assert.True(ForgeDivisions.IsDivisionKey("forge.tech")); Assert.False(ForgeDivisions.IsDivisionKey("forge.unknown"));
    }

    [Fact]
    public async Task Install_creates_pending_drafts_only_is_idempotent_and_keeps_relationships_opt_in()
    {
        using var factory = new ForgeApiFactory(); using var owner = factory.Owner();
        var first = await LifeScenarioTests.Post<List<ForgeDivisionDraft>>(owner, Drafts, new InstallForgeDivisionsRequest());
        Assert.Equal(16, first.Count);
        Assert.Equal(15, first.Count(x => x.Status == "drafted"));
        Assert.Equal("skipped-optional", first.Single(x => x.Key == "forge.relationships").Status);
        Assert.Equal(DomainRiskClass.Finance, first.Single(x => x.Key == "forge.wealth").Risk);

        var domains = (await owner.GetFromJsonAsync<List<DomainPreview>>("/api/domains", LifeScenarioTests.Json))!
            .Where(d => d.Definition.Key.StartsWith(ForgeDivisions.KeyPrefix, StringComparison.Ordinal)).ToArray();
        Assert.Equal(15, domains.Length);
        Assert.All(domains, d => {
            Assert.NotEqual(DomainStatus.Active, d.Definition.Status); // never activated by install
            Assert.Null(d.Definition.CurrentVersionId);
            Assert.False(d.SharesPersonalContext);
        });
        // The existing registry policy keeps health-class domains as drafts until trusted medical sources exist.
        // PF-02 must not weaken that: HEALTH stays un-activatable; every other division is ready for owner approval.
        var health = domains.Single(d => d.Definition.Key == "forge.health");
        Assert.Equal(DomainStatus.Draft, health.Definition.Status);
        Assert.Contains(health.UnmetDependencies, x => x.Contains("Trusted medical sources", StringComparison.Ordinal));
        Assert.All(domains.Where(d => d.Definition.Key != "forge.health"), d => {
            Assert.Equal(DomainStatus.PendingApproval, d.Definition.Status);
            Assert.Empty(d.UnmetDependencies);
        });

        var again = await LifeScenarioTests.Post<List<ForgeDivisionDraft>>(owner, Drafts, new InstallForgeDivisionsRequest());
        Assert.Equal(15, again.Count(x => x.Status == "existing"));
        Assert.Equal(15, (await owner.GetFromJsonAsync<List<DomainPreview>>("/api/domains", LifeScenarioTests.Json))!
            .Count(d => d.Definition.Key.StartsWith(ForgeDivisions.KeyPrefix, StringComparison.Ordinal)));

        var optional = await LifeScenarioTests.Post<List<ForgeDivisionDraft>>(owner, Drafts, new InstallForgeDivisionsRequest(IncludeOptional: true));
        Assert.Equal("drafted", optional.Single(x => x.Key == "forge.relationships").Status);
        Assert.Equal(15, optional.Count(x => x.Status == "existing"));
    }

    [Fact]
    public async Task Install_never_overwrites_an_owner_edited_definition_and_concurrent_installs_deduplicate()
    {
        using var factory = new ForgeApiFactory(); using var owner = factory.Owner();
        var tech = ForgeDivisions.All.Single(d => d.Code == "TECH");
        var edited = ForgeDivisions.Manifest(tech) with { Description = "Owner wording for engineering practice." };
        var ownerDraft = await LifeScenarioTests.Post<DomainPreview>(owner, "/api/domains/drafts",
            new SaveDomainDraftRequest(Guid.NewGuid(), null, JsonSerializer.SerializeToElement(edited, DomainPolicy.Json)));

        // Two installs racing: the registry's per-request replay must keep one draft per division.
        var racing = await Task.WhenAll(
            LifeScenarioTests.Post<List<ForgeDivisionDraft>>(owner, Drafts, new InstallForgeDivisionsRequest()),
            LifeScenarioTests.Post<List<ForgeDivisionDraft>>(owner, Drafts, new InstallForgeDivisionsRequest()));
        Assert.All(racing, result => Assert.Equal("existing", result.Single(x => x.Key == "forge.tech").Status));

        var forge = (await owner.GetFromJsonAsync<List<DomainPreview>>("/api/domains", LifeScenarioTests.Json))!
            .Where(d => d.Definition.Key.StartsWith(ForgeDivisions.KeyPrefix, StringComparison.Ordinal)).ToArray();
        Assert.Equal(15, forge.Length);
        Assert.Equal(15, forge.Select(d => d.Definition.Key).Distinct(StringComparer.Ordinal).Count());
        var kept = forge.Single(d => d.Definition.Key == "forge.tech");
        Assert.Equal(ownerDraft.Definition.Revision, kept.Definition.Revision); // no new version added
        Assert.Equal(ownerDraft.Version.Id, kept.Version.Id);
        Assert.Equal("Owner wording for engineering practice.", kept.Version.Manifest.Description);
        Assert.NotEqual(ForgeDivisions.DraftActor, kept.Version.CreatedBy);
    }

    [Fact]
    public async Task Goal_deletion_tombstone_clears_the_division_tag()
    {
        using var factory = new ForgeApiFactory(); using var owner = factory.Owner();
        var now = DateTimeOffset.Parse("2026-11-30T12:00:00Z");
        var goal = await LifeScenarioTests.Post<PersonalGoal>(owner, "/api/goals", new SaveGoalRequest(Guid.NewGuid(), null, "Tagged",
            "Division tombstone", "practice-reminder", false, now.AddDays(1), now.AddDays(8), "Paused", DivisionKey: "forge.voice"));
        using var deleted = await owner.DeleteAsync($"/api/goals/{goal.Id}?revision={goal.Revision}");
        Assert.True(deleted.IsSuccessStatusCode);
        var tombstone = await factory.Services.GetRequiredService<Jarvis.Orchestrator.Runtime.IRuntimeReader>().ReadAsync(s => s.Goals[goal.Id]);
        Assert.Equal(GoalStatus.Deleted, tombstone.Status);
        Assert.Null(tombstone.DivisionKey);
        Assert.Equal("", tombstone.Title); Assert.Equal("", tombstone.Brief);
    }

    [Fact]
    public async Task Install_is_unavailable_while_forge_is_disabled()
    {
        using var factory = new ForgeApiFactory(enabled: false); using var owner = factory.Owner();
        using var response = await owner.PostAsJsonAsync(Drafts, new InstallForgeDivisionsRequest());
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Goals_accept_an_optional_known_division_tag_and_keep_the_legacy_shape()
    {
        using var factory = new ForgeApiFactory(); using var owner = factory.Owner();
        var now = DateTimeOffset.Parse("2026-11-30T12:00:00Z");
        var plain = new SaveGoalRequest(Guid.NewGuid(), null, "Untagged", "No division", "practice-reminder", false, now.AddDays(1), now.AddDays(8), "Paused");
        var created = await LifeScenarioTests.Post<PersonalGoal>(owner, "/api/goals", plain);
        Assert.Null(created.DivisionKey);
        Assert.DoesNotContain("divisionKey", JsonSerializer.Serialize(created, LifeScenarioTests.Json), StringComparison.OrdinalIgnoreCase);

        var tagged = plain with { RequestId = Guid.NewGuid(), Title = "Tagged", DivisionKey = "forge.tech" };
        var goal = await LifeScenarioTests.Post<PersonalGoal>(owner, "/api/goals", tagged);
        Assert.Equal("forge.tech", goal.DivisionKey);
        Assert.Equal(goal, await LifeScenarioTests.Post<PersonalGoal>(owner, "/api/goals", tagged)); // identical retry replays

        using (var changedTag = await owner.PostAsJsonAsync("/api/goals", tagged with { DivisionKey = "forge.career" }))
            Assert.Equal(HttpStatusCode.Conflict, changedTag.StatusCode); // reused ID with different tag
        using (var unknown = await owner.PostAsJsonAsync("/api/goals", tagged with { RequestId = Guid.NewGuid(), DivisionKey = "forge.unknown" }))
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);

        var retagged = await LifeScenarioTests.Post<PersonalGoal>(owner, "/api/goals", tagged with { ExpectedRevision = goal.Revision, DivisionKey = "forge.career" });
        Assert.Equal("forge.career", retagged.DivisionKey);
        using var deleted = await owner.DeleteAsync($"/api/goals/{goal.Id}?revision={retagged.Revision}");
        Assert.True(deleted.IsSuccessStatusCode);
        var export = await owner.GetFromJsonAsync<CoreDataExport>("/api/privacy/export", LifeScenarioTests.Json);
        Assert.DoesNotContain(export!.Goals, g => g.Id == goal.Id); // deleted goals stay out of export
    }
}
