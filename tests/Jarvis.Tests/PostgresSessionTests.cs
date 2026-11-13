using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jarvis.Contracts;
using Jarvis.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Jarvis.Tests;

// Opt-in integration tests: create and drop only a randomly named test database.
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JARVIS_TEST_POSTGRES")))
            Skip = "Set JARVIS_TEST_POSTGRES to a PostgreSQL connection with CREATE DATABASE permission.";
    }
}

public sealed partial class PostgresSessionTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [PostgresFact]
    public async Task Session_and_checkpoints_survive_api_restart()
    {
        await using var database = await TestDatabase.CreateAsync();
        ConversationSession saved;
        using (var first = new PostgresApiFactory(database.ConnectionString))
        using (var client = first.AuthorisedClient())
        {
            var created = await CreateSession(client);
            var response = await client.PostAsJsonAsync($"/api/sessions/{created.Id}/checkpoints",
                new AddCheckpointRequest("Saved before restart", "Resume this task"), Json);
            response.EnsureSuccessStatusCode();
            saved = (await response.Content.ReadFromJsonAsync<ConversationSession>(Json))!;
        }

        // A fresh host, service provider, store and context; only PostgreSQL is shared.
        using var second = new PostgresApiFactory(database.ConnectionString);
        using var restarted = second.AuthorisedClient();
        var restored = await restarted.GetFromJsonAsync<ConversationSession>($"/api/sessions/{saved.Id}", Json);
        Assert.Equal(JsonSerializer.Serialize(saved, Json), JsonSerializer.Serialize(restored, Json));
        var list = await restarted.GetFromJsonAsync<List<ConversationSession>>("/api/sessions", Json);
        Assert.Equal(saved.Id, Assert.Single(list!).Id);
        var next = await restarted.PostAsJsonAsync($"/api/sessions/{saved.Id}/checkpoints",
            new AddCheckpointRequest("Resumed", null), Json);
        next.EnsureSuccessStatusCode();
        var updated = (await next.Content.ReadFromJsonAsync<ConversationSession>(Json))!;
        Assert.Equal([1, 2], updated.Checkpoints.Select(c => c.Sequence));
        Assert.Equal("Resume this task", updated.CurrentTask);
    }

    [PostgresFact]
    public async Task Concurrent_hosts_do_not_lose_or_duplicate_checkpoints()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var first = new PostgresApiFactory(database.ConnectionString);
        using var second = new PostgresApiFactory(database.ConnectionString);
        using var a = first.AuthorisedClient();
        using var b = second.AuthorisedClient();
        var session = await CreateSession(a);
        var responses = await Task.WhenAll(Enumerable.Range(1, 20).Select(i =>
            (i % 2 == 0 ? a : b).PostAsJsonAsync($"/api/sessions/{session.Id}/checkpoints",
                new AddCheckpointRequest($"Checkpoint {i}", null), Json)));
        foreach (var response in responses)
        {
            using (response) response.EnsureSuccessStatusCode();
        }
        var result = (await a.GetFromJsonAsync<ConversationSession>($"/api/sessions/{session.Id}", Json))!;
        Assert.Equal(Enumerable.Range(1, 20), result.Checkpoints.Select(c => c.Sequence));
        Assert.Equal(20, result.Checkpoints.Select(c => c.Summary).Distinct().Count());
    }

    [PostgresFact]
    public async Task Failed_checkpoint_rolls_back_and_unknown_session_returns_404()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var host = new PostgresApiFactory(database.ConnectionString);
        using var client = host.AuthorisedClient();
        var session = await CreateSession(client);
        var store = new PostgresSessionStore(host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>());
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.AppendCheckpointAsync(
            session.Id, _ => throw new InvalidOperationException("Failed callback"), DateTimeOffset.UtcNow));
        var response = await client.PostAsJsonAsync($"/api/sessions/{session.Id}/checkpoints",
            new AddCheckpointRequest("After rollback", null), Json);
        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<ConversationSession>(Json))!;
        Assert.Equal(1, Assert.Single(result.Checkpoints).Sequence);
        var missing = await client.PostAsJsonAsync($"/api/sessions/{Guid.NewGuid()}/checkpoints",
            new AddCheckpointRequest("Missing", null), Json);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    private static async Task<ConversationSession> CreateSession(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/sessions",
            new CreateSessionRequest(ActivityType.LeetCode, "Persistent session", "test-device"), Json);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ConversationSession>(Json))!;
    }

    private sealed class PostgresApiFactory(string connection) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Jarvis:ApiKey", JarvisApiFactory.TestKey);
            builder.UseSetting("Jarvis:StorageProvider", "Postgres");
            builder.UseSetting("ConnectionStrings:Jarvis", connection);
        }

        public HttpClient AuthorisedClient()
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey);
            return client;
        }
    }

    private sealed class TestDatabase(string adminConnection, string name, string connection) : IAsyncDisposable
    {
        public string ConnectionString => connection;

        public static async Task<TestDatabase> CreateAsync()
        {
            var admin = Environment.GetEnvironmentVariable("JARVIS_TEST_POSTGRES")!;
            var name = "jarvis_test_" + Guid.NewGuid().ToString("N");
            await using var db = new NpgsqlConnection(admin);
            await db.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", db);
            await command.ExecuteNonQueryAsync();
            return new TestDatabase(admin, name, new NpgsqlConnectionStringBuilder(admin)
            {
                Database = name, Pooling = false
            }.ConnectionString);
        }

        public async ValueTask DisposeAsync()
        {
            await using var db = new NpgsqlConnection(adminConnection);
            await db.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", db);
            await command.ExecuteNonQueryAsync();
        }
    }
}
