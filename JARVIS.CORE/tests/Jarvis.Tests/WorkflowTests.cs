using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed class WorkflowTests
{
    private readonly TestWorkflowClock _clock = new();
    private readonly InMemoryWorkflowStore _store = new();
    private WorkflowService Service => new(_store, _clock);

    [Fact]
    public async Task Concurrent_replayed_trigger_is_one_run_with_one_initial_event()
    {
        var request = new CreatePracticePlanRequest(Guid.NewGuid());
        var runs = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Service.CreateAsync(request)));
        Assert.Single(runs.Select(x => x.Id).Distinct());
        Assert.Single(await _store.ListAsync());
        Assert.Single(await _store.EventsAsync(request.RequestId, 0));
        var conflict = await Assert.ThrowsAsync<DomainException>(() => Service.CreateAsync(request with { NotBefore = _clock.GetUtcNow().AddHours(1) }));
        Assert.Equal(409, conflict.StatusCode);
    }

    [Fact]
    public async Task Only_one_worker_claims_and_expired_worker_cannot_publish_or_renew()
    {
        var run = await Service.CreateAsync(new(Guid.NewGuid()));
        var claims = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Service.ClaimAsync($"worker-{i}")));
        var first = Assert.Single(claims, x => x is not null)!;
        _clock.Advance(TimeSpan.FromSeconds(31));
        var second = (await Service.ClaimAsync("replacement"))!;
        Assert.NotEqual(first.ClaimToken, second.ClaimToken);
        Assert.Equal(2, second.Attempt);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => Service.CompleteAsync(run.Id, first.ClaimToken!.Value, new([], [])))).StatusCode);
        await Assert.ThrowsAsync<DomainException>(() => Service.HeartbeatAsync(run.Id, first.ClaimToken!.Value));
        await Service.CompleteAsync(run.Id, second.ClaimToken!.Value, new([], []));
        Assert.Equal(WorkflowStatus.Succeeded, (await _store.GetAsync(run.Id))!.Status);
        Assert.Equal(4, (await _store.EventsAsync(run.Id, 0)).Count);
    }

    [Fact]
    public async Task Cancellation_revokes_inflight_claim_and_retries_are_idempotent()
    {
        var run = await Service.CreateAsync(new(Guid.NewGuid()));
        var claimed = (await Service.ClaimAsync("worker"))!;
        var cancelled = await Service.CancelAsync(run.Id);
        var repeated = await Service.CancelAsync(run.Id);
        Assert.Equal(cancelled.Revision, repeated.Revision);
        Assert.Null(cancelled.ClaimToken);
        await Assert.ThrowsAsync<DomainException>(() => Service.CompleteAsync(run.Id, claimed.ClaimToken!.Value, new([], [])));
        Assert.Null(await Service.ClaimAsync("worker"));
        Assert.Equal(3, (await _store.EventsAsync(run.Id, 0)).Count);
    }

    [Fact]
    public async Task Lease_expiry_rejects_all_old_writes_even_before_another_worker_claims()
    {
        var run = await Service.CreateAsync(new(Guid.NewGuid()));
        var claim = (await Service.ClaimAsync("old-worker"))!;
        _clock.Advance(WorkflowService.LeaseDuration);
        var token = claim.ClaimToken!.Value;
        await Assert.ThrowsAsync<DomainException>(() => Service.CompleteAsync(run.Id, token, new([], [])));
        await Assert.ThrowsAsync<DomainException>(() => Service.HeartbeatAsync(run.Id, token));
        await Assert.ThrowsAsync<DomainException>(() => Service.FailAsync(run.Id, token, true));
        Assert.Equal(2, (await _store.EventsAsync(run.Id, 0)).Count);
    }

    [Fact]
    public async Task Permanent_failure_is_not_retried_and_completed_runs_cannot_be_cancelled()
    {
        var run = await Service.CreateAsync(new(Guid.NewGuid()));
        var claim = (await Service.ClaimAsync("worker"))!;
        var failed = await Service.FailAsync(run.Id, claim.ClaimToken!.Value, false);
        Assert.Equal("processing_failed", failed.ErrorCode);
        Assert.Equal(WorkflowStatus.Failed, failed.Status);
        Assert.Equal(failed, await Service.CancelAsync(run.Id));
        Assert.Null(await Service.ClaimAsync("worker"));
    }

    [Fact]
    public async Task Transient_failures_back_off_and_stop_after_three_attempts()
    {
        var run = await Service.CreateAsync(new(Guid.NewGuid()));
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var claimed = (await Service.ClaimAsync("worker"))!;
            Assert.Equal(attempt, claimed.Attempt);
            var failed = await Service.FailAsync(run.Id, claimed.ClaimToken!.Value, true);
            if (attempt < 3)
            {
                Assert.Equal(WorkflowStatus.RetryScheduled, failed.Status);
                Assert.Null(await Service.ClaimAsync("early"));
                _clock.Advance(TimeSpan.FromSeconds(4));
            }
            else Assert.Equal(WorkflowStatus.Failed, failed.Status);
        }
        Assert.Null(await Service.ClaimAsync("late"));
    }

    [Fact]
    public async Task Future_runs_wait_and_stale_work_expires_without_processing()
    {
        var request = new CreatePracticePlanRequest(Guid.NewGuid(), _clock.GetUtcNow().AddHours(1));
        var run = await Service.CreateAsync(request);
        Assert.Null(await Service.ClaimAsync("early"));
        _clock.Advance(TimeSpan.FromHours(2));
        var expired = (await Service.ClaimAsync("late"))!;
        Assert.Equal(WorkflowStatus.Expired, expired.Status);
        Assert.Equal(0, expired.Attempt);
        // Old request retries still return the canonical expired run.
        Assert.Equal(run.Id, (await Service.CreateAsync(request)).Id);
        Assert.Null(await Service.ClaimAsync("late"));
    }

    [Fact]
    public async Task Repeated_worker_crashes_exhaust_attempts_and_heartbeat_extends_a_valid_lease()
    {
        var run = await Service.CreateAsync(new(Guid.NewGuid()));
        var claimed = (await Service.ClaimAsync("worker"))!;
        _clock.Advance(TimeSpan.FromSeconds(20));
        var renewed = await Service.HeartbeatAsync(run.Id, claimed.ClaimToken!.Value);
        Assert.True(renewed.LeaseExpiresAt > claimed.LeaseExpiresAt);
        Assert.Null(await Service.ClaimAsync("competitor"));
        for (var i = 0; i < 3; i++)
        {
            _clock.Advance(TimeSpan.FromSeconds(31));
            claimed = (await Service.ClaimAsync("recovery"))!;
        }
        Assert.Equal(WorkflowStatus.Failed, claimed.Status);
        Assert.Equal(3, claimed.Attempt);
    }

    [Fact]
    public async Task Practice_plan_uses_saved_due_prompts_and_story_without_answers_or_invented_claims()
    {
        var state = new InMemoryRuntimeStore();
        var now = _clock.GetUtcNow();
        await state.UpdateAsync(s =>
        {
            foreach (var (domain, due) in new[] { (LearningDomain.SystemDesign, now), (LearningDomain.Communication, now),
                (LearningDomain.French, now), (LearningDomain.LeetCode, now.AddDays(1)) })
            {
                var id = Guid.NewGuid(); s.Learning[id] = new(id, domain, domain + " prompt", "Hidden answer", 1, 5, due, now);
            }
            var storyId = Guid.NewGuid();
            s.Stories[storyId] = new(storyId, new("FX story", "Actual employer", "Actual feature"), 1, now, now);
            return true;
        });
        var result = await new PracticePlanProcessor(state, _clock).BuildAsync();
        Assert.Equal(3, result.Steps.Count);
        Assert.Empty(result.Gaps);
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("Hidden answer", json);
        Assert.DoesNotContain("French", json);
        Assert.DoesNotContain("LeetCode", json);
        Assert.DoesNotContain("FX story", json);
        Assert.DoesNotContain("Actual employer", json);
        Assert.Single(result.Steps, x => x.Kind == "story");
        var empty = await new PracticePlanProcessor(new InMemoryRuntimeStore(), _clock).BuildAsync();
        Assert.Empty(empty.Steps);
        Assert.Equal(2, empty.Gaps.Count);
    }

    [Fact]
    public async Task Http_contract_protects_runs_hides_claims_and_replays_events()
    {
        using var original = new JarvisApiFactory();
        using var host = original.WithWebHostBuilder(b => b.UseSetting("Jarvis:WorkflowsEnabled", "false"));
        using var client = host.CreateClient(); client.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey);
        var request = new CreatePracticePlanRequest(Guid.NewGuid());
        using var response = await client.PostAsJsonAsync("/api/workflows/practice-plans", request);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.EndsWith(request.RequestId.ToString(), response.Headers.Location!.ToString());
        var json = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("claimToken", json); Assert.DoesNotContain("fingerprint", json); Assert.DoesNotContain("workerId", json);
        var reader = host.Services.GetRequiredService<IWorkflowReader>();
        var before = await reader.GetAsync(request.RequestId);
        using var get = await client.GetAsync($"/api/workflows/{request.RequestId}"); get.EnsureSuccessStatusCode();
        Assert.Equal(before, await reader.GetAsync(request.RequestId));
        using var cancel = await client.PostAsJsonAsync($"/api/workflows/{request.RequestId}/cancel", new { }); cancel.EnsureSuccessStatusCode();
        var events = await client.GetFromJsonAsync<List<WorkflowEvent>>($"/api/workflows/{request.RequestId}/events?afterRevision=1", LifeScenarioTests.Json);
        Assert.Equal(WorkflowStatus.Cancelled, Assert.Single(events!).Status);
        using var missing = await client.GetAsync($"/api/workflows/{Guid.NewGuid()}/events");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        using var invalid = await client.PostAsJsonAsync("/api/workflows/practice-plans", new CreatePracticePlanRequest(Guid.Empty));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var anonymous = host.CreateClient();
        using var denied = await anonymous.GetAsync("/api/workflows");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
    }

    [Fact]
    public async Task Hosted_worker_commits_a_real_plan_and_one_completion_event()
    {
        using var host = new JarvisApiFactory(); using var client = host.CreateAuthorisedClient();
        var id = Guid.NewGuid();
        using var created = await client.PostAsJsonAsync("/api/workflows/practice-plans", new CreatePracticePlanRequest(id));
        created.EnsureSuccessStatusCode();
        var reader = host.Services.GetRequiredService<IWorkflowReader>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        WorkflowRun? result;
        while ((result = await reader.GetAsync(id, deadline.Token))!.Status != WorkflowStatus.Succeeded)
            await Task.Delay(100, deadline.Token);
        Assert.NotNull(result.Result);
        Assert.Equal(1, result.Attempt);
        Assert.Single(await reader.EventsAsync(id, 0), e => e.Status == WorkflowStatus.Succeeded);
    }
}

internal sealed class TestWorkflowClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}
