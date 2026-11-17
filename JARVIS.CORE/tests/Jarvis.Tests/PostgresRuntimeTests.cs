using System.Net.Http.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Jarvis.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task Story_bank_survives_restart_and_coordinates_edits_across_hosts()
    {
        await using var database = await TestDatabase.CreateAsync();
        var id = Guid.NewGuid();
        using (var a = new PostgresApiFactory(database.ConnectionString))
        using (var b = new PostgresApiFactory(database.ConnectionString))
        using (var clientA = a.AuthorisedClient())
        using (var clientB = b.AuthorisedClient())
        {
            var draft = new StoryContent("FX module", "Alternative Bank", "FX transaction service");
            await LifeScenarioTests.Post<Story>(clientA, "/api/stories", new CreateStoryRequest(id, draft));
            var responses = await Task.WhenAll(clientA.PutAsJsonAsync($"/api/stories/{id}", new UpdateStoryRequest(1, draft with { Result = "Recovered pending transactions" })),
                clientB.PutAsJsonAsync($"/api/stories/{id}", new UpdateStoryRequest(1, draft with { Result = "Prevented duplicate processing" })));
            try
            {
                Assert.Single(responses, r => r.StatusCode == System.Net.HttpStatusCode.OK);
                Assert.Single(responses, r => r.StatusCode == System.Net.HttpStatusCode.Conflict);
            }
            finally { foreach (var response in responses) response.Dispose(); }
        }
        using var restarted = new PostgresApiFactory(database.ConnectionString);
        using var client = restarted.AuthorisedClient();
        var restored = await client.GetFromJsonAsync<Story>($"/api/stories/{id}");
        Assert.Equal(2, restored!.Version);
        Assert.False(string.IsNullOrWhiteSpace(restored.Content.Result));
    }

    [PostgresFact]
    public async Task Existing_milestone_two_sessions_upgrade_without_losing_data()
    {
        await using var database = await TestDatabase.CreateAsync();
        var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        await using (var db = new SessionDbContext(new DbContextOptionsBuilder<SessionDbContext>().UseNpgsql(database.ConnectionString).Options))
        {
            await db.GetService<IMigrator>().MigrateAsync("20261030034425_InitialSessions");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Sessions" ("Id","Activity","Title","Status","CreatedAt","UpdatedAt")
                VALUES ({id},0,'Existing milestone two session',0,{now},{now})
                """);
        }
        using var host = new PostgresApiFactory(database.ConnectionString); using var client = host.AuthorisedClient();
        var session = await client.GetFromJsonAsync<ConversationSession>($"/api/sessions/{id}", Json);
        Assert.Equal("Existing milestone two session", session!.Title); Assert.Empty(session.Messages!); Assert.Null(session.Handoff);
        Assert.Null(session.VoiceSummary);
        var updated = await LifeScenarioTests.Post<ConversationSession>(client, $"/api/sessions/{id}/checkpoints", new AddCheckpointRequest("After upgrade", null));
        Assert.Single(updated.Checkpoints);
    }

    [PostgresFact]
    public async Task Devices_learning_transcript_and_pending_handoff_survive_restart()
    {
        await using var database = await TestDatabase.CreateAsync();
        Guid sessionId, cardId, handoffId;
        using (var first = new PostgresApiFactory(database.ConnectionString).WithWebHostBuilder(b =>
            b.ConfigureServices(s => s.AddSingleton<IConversationProvider>(new ScenarioConversation()))))
        using (var client = first.CreateClient())
        {
            client.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey);
            TestOwnerManagement.Authorize(client);
            await LifeScenarioTests.Register(client, "laptop"); await LifeScenarioTests.Register(client, "phone");
            var session = await LifeScenarioTests.Session(client); sessionId = session.Id;
            await LifeScenarioTests.Post<ConversationSession>(client, $"/api/sessions/{sessionId}/claim", new { deviceId = "laptop" });
            await LifeScenarioTests.Post<ConversationSession>(client, $"/api/sessions/{sessionId}/messages", new ChatRequest(Guid.NewGuid(), "Remember binary search"));
            var transfer = await LifeScenarioTests.Post<ConversationSession>(client, $"/api/sessions/{sessionId}/handoff", new StartHandoffRequest("laptop", "phone", "Resume binary search")); handoffId = transfer.Handoff!.Id;
            var card = await LifeScenarioTests.Post<LearningItem>(client, "/api/learning", new CreateLearningRequest(LearningDomain.LeetCode, "Binary search complexity?", "O(log n)")); cardId = card.Id;
            await LifeScenarioTests.Post<LearningItem>(client, $"/api/learning/{cardId}/reviews", new ReviewLearningRequest(Guid.NewGuid(), ReviewRating.Good, 2));
        }
        using var second = new PostgresApiFactory(database.ConnectionString); using var restarted = second.AuthorisedClient();
        var restored = (await restarted.GetFromJsonAsync<ConversationSession>($"/api/sessions/{sessionId}", Json))!;
        Assert.Equal(handoffId, restored.Handoff!.Id); Assert.Equal(2, restored.Messages!.Count); Assert.Single(restored.Checkpoints);
        Assert.Equal(2, (await restarted.GetFromJsonAsync<List<DeviceView>>("/api/devices", Json))!.Count);
        Assert.Single((await restarted.GetFromJsonAsync<List<ReviewEvent>>($"/api/learning/{cardId}/reviews", Json))!);
        await LifeScenarioTests.Post<RegisteredDevice>(restarted, "/api/devices/phone/heartbeat", new { });
        var ready = await LifeScenarioTests.Post<ConversationSession>(restarted, $"/api/sessions/{sessionId}/handoff/ready", new HandoffActionRequest(handoffId, "phone"));
        Assert.Equal(HandoffState.TargetReady, ready.Handoff!.State);
    }

    [PostgresFact]
    public async Task Tool_confirmation_across_two_hosts_is_executed_once_and_audit_survives_restart()
    {
        await using var database = await TestDatabase.CreateAsync(); var home = new ScenarioHome(); Guid toolId;
        using (var originalA = new PostgresApiFactory(database.ConnectionString))
        using (var originalB = new PostgresApiFactory(database.ConnectionString))
        using (var a = originalA.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IHomeControl>(home))))
        using (var b = originalB.WithWebHostBuilder(builder => builder.ConfigureServices(s => s.AddSingleton<IHomeControl>(home))))
        using (var clientA = a.CreateClient())
        using (var clientB = b.CreateClient())
        {
            clientA.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey); clientB.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey);
            TestOwnerManagement.Authorize(clientA); TestOwnerManagement.Authorize(clientB);
            var session = await LifeScenarioTests.Session(clientA);
            var tool = await LifeScenarioTests.Post<AuditedTool>(clientA, "/api/tools/home", new HomeActionRequest(Guid.NewGuid(), session.Id, "light.desk", "turn_on")); toolId = tool.Invocation.Id;
            await Task.WhenAll(Enumerable.Range(0, 10).Select(i => LifeScenarioTests.Post<AuditedTool>(i % 2 == 0 ? clientA : clientB, $"/api/tools/{toolId}/confirm", new { approve = true })));
            Assert.Equal(1, home.Calls);
        }
        using var restarted = new PostgresApiFactory(database.ConnectionString); using var client = restarted.AuthorisedClient();
        var audit = Assert.Single((await client.GetFromJsonAsync<List<AuditedTool>>("/api/tools", Json))!);
        Assert.Equal(toolId, audit.Invocation.Id); Assert.Equal(ToolInvocationStatus.Succeeded, audit.Invocation.Status);
    }

    [PostgresFact]
    public async Task Concurrent_learning_reviews_are_not_lost()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var a = new PostgresApiFactory(database.ConnectionString); using var b = new PostgresApiFactory(database.ConnectionString);
        using var clientA = a.AuthorisedClient(); using var clientB = b.AuthorisedClient();
        var card = await LifeScenarioTests.Post<LearningItem>(clientA, "/api/learning", new CreateLearningRequest(LearningDomain.French, "Thank you", "Merci"));
        await Task.WhenAll(Enumerable.Range(0, 12).Select(i => LifeScenarioTests.Post<LearningItem>(i % 2 == 0 ? clientA : clientB,
            $"/api/learning/{card.Id}/reviews", new ReviewLearningRequest(Guid.NewGuid(), ReviewRating.Good, 1))));
        Assert.Equal(12, (await clientA.GetFromJsonAsync<List<ReviewEvent>>($"/api/learning/{card.Id}/reviews", Json))!.Count);
    }
}
