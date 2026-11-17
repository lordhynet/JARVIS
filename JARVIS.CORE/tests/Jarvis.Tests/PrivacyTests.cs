using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jarvis.Api.Integrations;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Jarvis.Orchestrator.Training;
using Jarvis.Orchestrator.Workflows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed class PrivacyTests
{
    [Fact]
    public async Task Mobile_epoch_fence_rejects_stale_and_invalid_writes_before_dispatch()
    {
        using var factory = new JarvisApiFactory(); using var client = factory.CreateAuthorisedClient();
        var status = (await client.GetFromJsonAsync<PrivacyStatus>("/api/privacy/status", LifeScenarioTests.Json))!;
        foreach (var supplied in new[] { "invalid", "-1", (status.Epoch + 1).ToString() })
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/sessions") { Content = JsonContent.Create(new { activity = "General", title = "Must not be stored" }) };
            request.Headers.Add("X-Jarvis-Data-Epoch", supplied);
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            Assert.Equal(status.Epoch.ToString(), Assert.Single(response.Headers.GetValues("X-Jarvis-Data-Epoch")));
            Assert.Contains("coreEpochChanged", await response.Content.ReadAsStringAsync());
        }
        Assert.Empty(await factory.Services.GetRequiredService<ISessionStore>().ListAsync());
        client.DefaultRequestHeaders.Add("X-Jarvis-Data-Epoch", status.Epoch.ToString());
        using var accepted = await client.GetAsync("/api/sessions"); accepted.EnsureSuccessStatusCode();
        Assert.Equal(status.Epoch.ToString(), Assert.Single(accepted.Headers.GetValues("X-Jarvis-Data-Epoch")));
        await LifeScenarioTests.Post<PrivacyStatus>(client, "/api/privacy/erase", new EraseCoreRequest(Guid.NewGuid(), status.Revision, PrivacyData.ErasePhrase));
        using var stale = await client.GetAsync("/api/sessions"); Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var maintenance = await client.GetAsync("/api/privacy/status"); maintenance.EnsureSuccessStatusCode();
    }
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-13T12:00Z");
    private static ConversationSession Session(Guid id, string title, SessionStatus status = SessionStatus.Active, DateTimeOffset? at = null, IReadOnlyList<ChatMessage>? messages = null) =>
        new(id, ActivityType.General, title, status, null, null, at ?? Now, at ?? Now, [], Messages: messages ?? []);

    [Fact]
    public async Task Export_covers_all_stores_without_configuration_claims_or_voice_credentials()
    {
        using var factory = new JarvisApiFactory(); using var client = factory.CreateAuthorisedClient();
        var services = factory.Services; var session = Session(Guid.NewGuid(), "My private session");
        await services.GetRequiredService<ISessionStore>().AddAsync(session);
        await services.GetRequiredService<IRuntimeStore>().UpdateAsync(s =>
        {
            s.Stories[Guid.NewGuid()] = new(Guid.NewGuid(), new("My private story", "My company", "My service"), 1, Now, Now);
            s.VoiceTickets["secret-ticket-hash"] = new("secret-ticket-hash", new(session.Id, "device", 9, Now.AddHours(1)), Now.AddHours(1));
            s.ProviderResults[Guid.NewGuid()] = new(Guid.NewGuid(), session.Id, "request-fingerprint", "{\"quote\":\"My private derived feedback\"}", Now);
            return true;
        });
        await services.GetRequiredService<IWorkflowStore>().AddAsync(new(Guid.NewGuid(), "practice-plan", 1, "internal-request-fingerprint", WorkflowStatus.Running, 1, 1,
            Now, Now, Now.AddHours(1), null, ClaimToken: Guid.NewGuid(), WorkerId: "private-worker-claim", LeaseExpiresAt: Now.AddMinutes(1)));
        var response = await client.GetAsync("/api/privacy/export"); response.EnsureSuccessStatusCode(); var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("My private session", json); Assert.Contains("My private story", json); Assert.Contains("My private derived feedback", json);
        Assert.DoesNotContain(JarvisApiFactory.TestKey, json); Assert.DoesNotContain("private-worker-claim", json); Assert.DoesNotContain("secret-ticket-hash", json);
        Assert.DoesNotContain("claimToken", json); Assert.DoesNotContain("erasureKeyFingerprint", json);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition!.DispositionType);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task Erasure_requires_confirmation_keeps_spend_and_requires_rotated_key_before_reopen()
    {
        using var factory = new JarvisApiFactory(); using var client = factory.CreateAuthorisedClient();
        var services = factory.Services; await services.GetRequiredService<ISessionStore>().AddAsync(Session(Guid.NewGuid(), "Erase this"));
        await services.GetRequiredService<UsageBudget>().ReserveAsync(Guid.NewGuid(), Guid.NewGuid(), "fixture", .1m, new(1, 5, 5), default);
        var request = new EraseCoreRequest(Guid.NewGuid(), 1, PrivacyData.ErasePhrase);
        using var rejected = await client.PostAsJsonAsync("/api/privacy/erase", request with { Confirmation = "yes" }); Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Single(await services.GetRequiredService<ISessionStore>().ListAsync());
        var erased = await LifeScenarioTests.Post<PrivacyStatus>(client, "/api/privacy/erase", request); Assert.True(erased.Erased);
        Assert.Equal(erased, await LifeScenarioTests.Post<PrivacyStatus>(client, "/api/privacy/erase", request));
        using var blocked = await client.GetAsync("/api/sessions"); Assert.Equal(HttpStatusCode.ServiceUnavailable, blocked.StatusCode);
        var export = (await client.GetFromJsonAsync<CoreDataExport>("/api/privacy/export", LifeScenarioTests.Json))!;
        Assert.Empty(export.Sessions); Assert.Empty(export.Stories); Assert.Single(export.Usage); Assert.True(export.AgentControls.Paused);
        var reopen = new ReopenCoreRequest(erased.Revision, PrivacyData.ReopenPhrase);
        using var oldKey = await client.PostAsJsonAsync("/api/privacy/reopen", reopen); Assert.Equal(HttpStatusCode.Conflict, oldKey.StatusCode);
        services.GetRequiredService<IConfiguration>()["Jarvis:ApiKey"] = "rotated-core-test-key";
        client.DefaultRequestHeaders.Remove("X-Jarvis-Key"); client.DefaultRequestHeaders.Add("X-Jarvis-Key", "rotated-core-test-key");
        var opened = await LifeScenarioTests.Post<PrivacyStatus>(client, "/api/privacy/reopen", reopen);
        Assert.False(opened.Erased); Assert.True(opened.Epoch > erased.Epoch);
        Assert.Empty((await client.GetFromJsonAsync<List<ConversationSession>>("/api/sessions", LifeScenarioTests.Json))!);
    }

    [Fact]
    public async Task Old_work_cannot_read_new_generation_or_publish_to_any_store_after_reopen()
    {
        using var factory = new JarvisApiFactory(); using var client = factory.CreateAuthorisedClient(); var services = factory.Services;
        var privacy = services.GetRequiredService<IPrivacyStore>(); var key = PrivacyData.Hash("old-key");
        using var oldScope = PrivacyContext.Enter(0);
        var erased = await privacy.EraseAsync(new(Guid.NewGuid(), 1, PrivacyData.ErasePhrase), key, Now);
        using (PrivacyContext.Enter(erased.Epoch)) await privacy.ReopenAsync(new(erased.Revision, PrivacyData.ReopenPhrase), PrivacyData.Hash("new-key"));
        await Assert.ThrowsAsync<DomainException>(() => services.GetRequiredService<ISessionStore>().AddAsync(Session(Guid.NewGuid(), "Must not return")));
        await Assert.ThrowsAsync<DomainException>(() => services.GetRequiredService<ISessionStore>().ListAsync());
        await Assert.ThrowsAsync<DomainException>(() => services.GetRequiredService<IRuntimeStore>().UpdateAsync(s => { s.Learning.Clear(); return true; }));
        await Assert.ThrowsAsync<DomainException>(() => services.GetRequiredService<IWorkflowStore>().ListAsync());
        using (PrivacyContext.Enter((await privacy.StatusAsync()).Epoch)) Assert.Empty(await services.GetRequiredService<ISessionStore>().ListAsync());
    }

    [Fact]
    public async Task Retention_cascades_sessions_preserves_recent_corrections_and_purges_mirrored_voice()
    {
        using var factory = new JarvisApiFactory(); using var client = factory.CreateAuthorisedClient(); var services = factory.Services;
        var sessions = services.GetRequiredService<ISessionStore>(); var runtime = services.GetRequiredService<IRuntimeStore>(); var privacy = services.GetRequiredService<IPrivacyStore>();
        var oldId = Guid.NewGuid(); var activeId = Guid.NewGuid(); var voiceId = Guid.NewGuid(); var typedId = Guid.NewGuid();
        await sessions.AddAsync(Session(oldId, "Old ended", SessionStatus.Ended, Now.AddDays(-400)));
        await sessions.AddAsync(Session(activeId, "Old active", at: Now.AddDays(-400), messages: [new(voiceId, "user", "Retire this voice", Now.AddDays(-40)), new(typedId, "user", "Keep this typed text", Now.AddDays(-40))]) with
        {
            VoiceSummary = new([new(ChatRoles.User, "Retire this voice")])
        });
        var expired = Guid.NewGuid(); var corrected = Guid.NewGuid(); var cascade = Guid.NewGuid(); var q = QuestionBank.All[0];
        await runtime.UpdateAsync(s =>
        {
            TrainingAttempt Answer(Guid id, Guid session, DateTimeOffset last) => new(id, session, q.Id, 1, TrainingMode.RapidDrill, 1, 1, false, Now.AddDays(-200), [new(1, "Private answer", "text_only", last)], []);
            s.TrainingAttempts[expired] = Answer(expired, activeId, Now.AddDays(-200));
            s.TrainingAttempts[corrected] = Answer(corrected, activeId, Now.AddDays(-1));
            s.TrainingAttempts[cascade] = Answer(cascade, oldId, Now.AddDays(-1));
            s.VoiceTurns[voiceId] = new(voiceId, activeId, "user", "Retire this voice", 1, Now.AddDays(-40));
            var cached = Guid.NewGuid(); s.ProviderResults[cached] = new(cached, expired, "hash", "Private old quote", Now);
            return true;
        });
        var policy = new RetentionPolicy(); var preview = await privacy.PreviewAsync(policy, Now);
        Assert.Equal(1, preview.Eligible.Sessions); Assert.Equal(2, preview.Eligible.TrainingAnswers); Assert.Equal(1, preview.Eligible.VoiceTurns); Assert.Equal(1, preview.Eligible.ProviderResults);
        var request = new ApplyRetentionRequest(Guid.NewGuid(), Now, preview.PolicyFingerprint, PrivacyData.RetentionPhrase);
        var job = await privacy.RetainAsync(request, policy, Now); Assert.Equal(preview.Eligible, job.Removed);
        Assert.Equal(job, await privacy.RetainAsync(request, policy, Now.AddMinutes(1)));
        Assert.Null(await sessions.GetAsync(oldId)); Assert.NotNull(await sessions.GetAsync(activeId));
        Assert.Single((await sessions.GetAsync(activeId))!.Messages!); Assert.Equal(typedId, (await sessions.GetAsync(activeId))!.Messages![0].RequestId);
        Assert.Null((await sessions.GetAsync(activeId))!.VoiceSummary);
        Assert.NotNull(await runtime.ReadAsync(s => s.TrainingAttempts[expired].DeletedAt)); Assert.Null(await runtime.ReadAsync(s => s.TrainingAttempts[corrected].DeletedAt));
        Assert.Empty(await runtime.ReadAsync(s => s.ProviderResults)); Assert.Contains(oldId, await runtime.ReadAsync(s => s.RetiredSessions));
        await Assert.ThrowsAsync<DomainException>(() => privacy.RetainAsync(request with { AsOf = Now.AddMinutes(-1) }, policy, Now));
    }

    [Fact]
    public async Task A_provider_response_started_before_erasure_cannot_recreate_deleted_content()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = new ProviderFactory(async _ => { started.TrySetResult(); await finish.Task.WaitAsync(TimeSpan.FromSeconds(15)); return new(HttpStatusCode.OK)
            { Content = JsonContent.Create(new { status = "completed", output = new[] { new { content = new[] { new { type = "output_text", text = "Old private response" } } } } }) }; });
        using var client = host.Client(); var session = await LifeScenarioTests.Post<ConversationSession>(client, "/api/sessions", new CreateSessionRequest(ActivityType.General, "Private", null));
        var reply = client.PostAsJsonAsync($"/api/sessions/{session.Id}/messages", new ChatRequest(Guid.NewGuid(), "Private prompt"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(15));
        var erased = await LifeScenarioTests.Post<PrivacyStatus>(client, "/api/privacy/erase", new EraseCoreRequest(Guid.NewGuid(), 1, PrivacyData.ErasePhrase));
        host.Services.GetRequiredService<IConfiguration>()["Jarvis:ApiKey"] = "new-provider-test-key";
        client.DefaultRequestHeaders.Remove("X-Jarvis-Key"); client.DefaultRequestHeaders.Add("X-Jarvis-Key", "new-provider-test-key");
        await LifeScenarioTests.Post<PrivacyStatus>(client, "/api/privacy/reopen", new ReopenCoreRequest(erased.Revision, PrivacyData.ReopenPhrase));
        finish.TrySetResult(); using var response = await reply; Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        var export = await host.Services.GetRequiredService<IPrivacyReader>().ExportAsync(Now); Assert.Empty(export.ProviderResults); Assert.Empty(export.Sessions); Assert.Single(export.Usage);
    }
    [Fact]
    public async Task Numeric_settlement_survives_erasure_but_stale_content_and_new_charges_are_blocked()
    {
        using var factory = new JarvisApiFactory(); using var client = factory.CreateAuthorisedClient();
        var services = factory.Services; var budget = services.GetRequiredService<UsageBudget>();
        var privacy = services.GetRequiredService<IPrivacyStore>(); var runtime = services.GetRequiredService<IRuntimeStore>();
        using var epoch = PrivacyContext.Enter(0);
        var id = Guid.NewGuid(); await budget.ReserveAsync(id, Guid.NewGuid(), "fixture", .1m, new(1, 5, 5), default);
        await privacy.EraseAsync(new(Guid.NewGuid(), 1, PrivacyData.ErasePhrase), PrivacyData.Hash("old-key"), Now);
        var settled = await budget.SettleAsync(id, .2m, 100, 20, default);
        Assert.True(settled.ReconciliationRequired); Assert.Equal(.2m, settled.ActualUsd);
        Assert.Equal(settled, await budget.SettleAsync(id, .2m, 100, 20, default));
        await Assert.ThrowsAsync<DomainException>(() => budget.SettleAsync(id, .3m, 100, 20, default));
        await Assert.ThrowsAsync<DomainException>(() => budget.SettleAsync(Guid.NewGuid(), .1m, 1, 1, default));
        await Assert.ThrowsAsync<DomainException>(() => runtime.UpdateAsync(state => { state.ProviderResults[id] = new(id, id, "hash", "private response", Now); return true; }));
        await Assert.ThrowsAsync<DomainException>(() => budget.ReserveAsync(Guid.NewGuid(), Guid.NewGuid(), "fixture", .1m, new(1, 5, 5), default));
        using (PrivacyContext.Enter((await privacy.StatusAsync()).Epoch))
        {
            var exported = await privacy.ExportAsync(Now); Assert.Empty(exported.ProviderResults);
            Assert.Equal(.2m, Assert.Single(exported.Usage).ActualUsd);
        }
    }

}

