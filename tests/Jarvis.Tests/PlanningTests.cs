using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;
using System.Net;
using System.Net.Http.Json;

namespace Jarvis.Tests;

public sealed class PlanningTests
{
    [Fact]
    public async Task Android_goal_contract_preserves_create_identity_and_fences_update_and_delete_retries()
    {
        using var factory = new JarvisApiFactory(); using var client = factory.CreateAuthorisedClient();
        var now = DateTimeOffset.UtcNow;
        var request = new SaveGoalRequest(Guid.NewGuid(), null, "Android goal <literal>", "One real example", "practice-reminder", false, now.AddDays(1), now.AddDays(8), "Paused");
        // Gson omits null constructor fields; verify Android's exact create shape as well as the typed retry.
        var created = await LifeScenarioTests.Post<PersonalGoal>(client, "/api/goals", new {
            requestId = request.RequestId.ToString(), title = request.Title, brief = request.Brief, workflow = request.Workflow,
            allowCloudProcessing = request.AllowCloudProcessing, nextReviewAt = request.NextReviewAt.UtcDateTime.ToString("O"),
            deadline = request.Deadline.UtcDateTime.ToString("O"), status = request.Status
        });
        Assert.Equal(created, await LifeScenarioTests.Post<PersonalGoal>(client, "/api/goals", request));
        var edit = request with { ExpectedRevision = created.Revision, Brief = "Updated progress" };
        var updated = await LifeScenarioTests.Post<PersonalGoal>(client, "/api/goals", edit);
        Assert.Equal(created.Revision + 1, updated.Revision); Assert.Equal(request.RequestId, updated.Id);
        using var lostAckRetry = await client.PostAsJsonAsync("/api/goals", edit);
        Assert.Equal(HttpStatusCode.Conflict, lostAckRetry.StatusCode);
        var visible = (await client.GetFromJsonAsync<List<PersonalGoal>>("/api/goals", LifeScenarioTests.Json))!;
        Assert.Equal(updated, Assert.Single(visible));
        using var staleDelete = await client.DeleteAsync($"/api/goals/{created.Id}?revision={created.Revision}");
        Assert.Equal(HttpStatusCode.Conflict, staleDelete.StatusCode);
        using var deleted = await client.DeleteAsync($"/api/goals/{updated.Id}?revision={updated.Revision}"); deleted.EnsureSuccessStatusCode();
        Assert.Empty((await client.GetFromJsonAsync<List<PersonalGoal>>("/api/goals", LifeScenarioTests.Json))!);
        using var recreate = await client.PostAsJsonAsync("/api/goals", request);
        Assert.Equal(HttpStatusCode.Conflict, recreate.StatusCode);
    }
    [Fact]
    public async Task Cloud_only_goal_requires_consent_even_when_paused()
    {
        using var factory = new JarvisApiFactory(); using var client = factory.CreateAuthorisedClient(); var now = DateTimeOffset.UtcNow;
        var request = new SaveGoalRequest(Guid.NewGuid(), null, "Research draft", "Fixture only", "research-review", false, now.AddDays(1), now.AddDays(8), "Paused");
        using var denied = await client.PostAsJsonAsync("/api/goals", request);
        Assert.Equal(HttpStatusCode.BadRequest, denied.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<List<PersonalGoal>>("/api/goals", LifeScenarioTests.Json))!);
        var goal = await LifeScenarioTests.Post<PersonalGoal>(client, "/api/goals", request with { AllowCloudProcessing = true });
        Assert.Equal("Paused", goal.Status); Assert.Null(goal.LastRunId);
    }
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-13T12:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
    [Fact]
    public async Task Events_deduplicate_supersede_fence_claims_and_expire()
    {
        var clock = new Clock(); var store = new InMemoryRuntimeStore(); var runtime = new AgentRuntime(store, clock);
        var input = new AgentTriggerRequest("calendar:fixture", "event-1", 1, "interview-preparation", clock.Now, clock.Now.AddHours(1), "Private interview", false, "correlation-1");
        var created = await runtime.IngestTriggerAsync(input); Assert.Equal(created, await runtime.IngestTriggerAsync(input));
        await Assert.ThrowsAsync<DomainException>(() => runtime.IngestTriggerAsync(input with { Brief = "different" }));
        Assert.Equal(1, await runtime.TickAsync(default)); Assert.Equal(0, await runtime.TickAsync(default));
        var claimed = (await runtime.ClaimAsync(default))!;
        var updated = await runtime.IngestTriggerAsync(input with { Revision = 2, DueAt = clock.Now.AddMinutes(1) });
        await Assert.ThrowsAsync<DomainException>(() => runtime.CompleteAsync(claimed.Id, claimed.ClaimToken!.Value, "obsolete", null, false, default));
        Assert.Equal("Cancelled", await store.ReadAsync(s => s.AgentRuns[claimed.RunId].Status));
        await Assert.ThrowsAsync<DomainException>(() => runtime.IngestTriggerAsync(input));
        clock.Now = clock.Now.AddMinutes(1); Assert.Equal(1, await runtime.TickAsync(default));
        var replacement = await store.ReadAsync(s => s.AgentTriggers[updated.Id]); Assert.NotEqual(claimed.RunId, replacement.RunId);
        clock.Now = clock.Now.AddHours(1); await runtime.TickAsync(default);
        Assert.Equal("Expired", await store.ReadAsync(s => s.AgentTriggers[updated.Id].Status));
        Assert.Empty(await store.ReadAsync(s => s.AgentOutbox));
    }
    [Fact]
    public async Task Goals_review_once_per_revision_and_deletion_blocks_recreation()
    {
        var clock = new Clock(); var store = new InMemoryRuntimeStore(); var runtime = new AgentRuntime(store, clock);
        var request = new SaveGoalRequest(Guid.NewGuid(), null, "Prepare", "Use genuine project examples", "interview-preparation", false, clock.Now, clock.Now.AddDays(10));
        var goal = await runtime.SaveGoalAsync(request); Assert.Equal(goal, await runtime.SaveGoalAsync(request));
        await runtime.ControlsAsync(new(Paused: true), default); Assert.Equal(0, await runtime.TickAsync(default));
        await runtime.ControlsAsync(new(), default); Assert.Equal(1, await runtime.TickAsync(default)); Assert.Equal(0, await runtime.TickAsync(default));
        var first = (await store.ReadAsync(s => s.Goals[goal.Id])).LastRunId;
        var changed = await runtime.SaveGoalAsync(request with { ExpectedRevision = 1, Brief = "New progress" });
        Assert.Equal("Cancelled", await store.ReadAsync(s => s.AgentRuns[first!.Value].Status));
        Assert.Equal(1, await runtime.TickAsync(default));
        await Assert.ThrowsAsync<DomainException>(() => runtime.DeleteGoalAsync(goal.Id, 1));
        await runtime.DeleteGoalAsync(goal.Id, changed.Revision);
        Assert.Empty(await store.ReadAsync(s => s.Goals[goal.Id].Brief));
        await Assert.ThrowsAsync<DomainException>(() => runtime.SaveGoalAsync(request));
    }
    [Fact]
    public async Task Billing_reconciliation_requires_evidence_waits_for_dispatch_and_never_replays()
    {
        var clock = new Clock(); var store = new InMemoryRuntimeStore(); var budget = new UsageBudget(store, clock, new TestBudgetSource(clock));
        var reconciler = new UsageReconciler(store, clock); var id = Guid.NewGuid();
        await budget.ReserveAsync(id, Guid.NewGuid(), "fixture", .1m, new(1, 5, 5), default);
        var request = new ReconcileUsageRequest(Guid.NewGuid(), .05m, "provider-statement-reference", "CONFIRM VERIFIED CHARGE");
        await Assert.ThrowsAsync<DomainException>(() => reconciler.ReconcileAsync(TestBudgetSource.Owner, id, request));
        clock.Now = clock.Now.AddMinutes(11);
        await Assert.ThrowsAsync<DomainException>(() => reconciler.ReconcileAsync(TestBudgetSource.Owner, id, request with { Confirmation = "yes" }));
        var result = await reconciler.ReconcileAsync(TestBudgetSource.Owner, id, request); Assert.Equal(result, await reconciler.ReconcileAsync(TestBudgetSource.Owner, id, request));
        await Assert.ThrowsAsync<DomainException>(() => reconciler.ReconcileAsync(TestBudgetSource.Owner, id, request with { ActualUsd = 0 }));
        Assert.Equal(.05m, await store.ReadAsync(s => s.UsageReservations[id].ActualUsd));
        await Assert.ThrowsAsync<DomainException>(() => budget.ReserveAsync(id, Guid.NewGuid(), "fixture", .1m, new(1, 5, 5), default));
    }
}
