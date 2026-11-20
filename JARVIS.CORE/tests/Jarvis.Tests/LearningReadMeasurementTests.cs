using System.Diagnostics;
using System.Text.Json;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    [RuntimeMeasurementFact]
    public async Task Measure_indexed_learning_reads_and_capture_plans()
    {
        var reports = new List<object>(); var cutoff = new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero);
        foreach (var count in new[] { 1_000, 2_000, 10_000 }) {
            await using var database = await TestDatabase.CreateAsync();
            var connection = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Pooling = true }.ConnectionString;
            try {
                using var original = new PostgresApiFactory(connection, manualAgents: true);
                using var host = original.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
                using var client = host.CreateClient();
                var contexts = host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>(); Guid id;
                await using (var db = await contexts.CreateDbContextAsync()) {
                    await db.Database.ExecuteSqlInterpolatedAsync($"""
                        INSERT INTO "RuntimeLearningRows" ("Kind","Id","Json","DueAt")
                        SELECT 'Learning',id,jsonb_build_object('Id',id,'Domain',2,'Prompt','Synthetic query fixture',
                            'Answer','Synthetic answer','Stability',0,'Difficulty',5,'DueAt',due,'CreatedAt',{cutoff.AddDays(-2)}),due
                        FROM (SELECT n,md5('learning-read-fixture-'||n::text)::uuid AS id,
                            CASE WHEN n<=10 THEN {cutoff.AddDays(-1)} ELSE {cutoff.AddDays(1)} END AS due
                            FROM generate_series(1,{count}) n) fixture
                        """);
                    await db.Database.ExecuteSqlRawAsync("ANALYZE \"RuntimeLearningRows\"");
                    id = await db.RuntimeLearningRows.OrderBy(x => x.DueAt).Select(x => x.Id).FirstAsync();
                }
                var reader = host.Services.GetRequiredService<IRuntimeReader>(); var cards = new List<double>(); var pages = new List<double>();
                for (var i = 0; i < 55; i++) {
                    var timer = Stopwatch.StartNew(); Assert.Equal(id, (await reader.ReadLearningItemAsync(id))!.Id);
                    if (i >= 5) cards.Add(timer.Elapsed.TotalMilliseconds);
                    timer.Restart(); var page = await reader.ReadDueLearningPageAsync(cutoff, 5);
                    Assert.Equal(5, page.Items.Count); Assert.Equal(6, page.CandidatesRead); Assert.NotNull(page.Next);
                    if (i >= 5) pages.Add(timer.Elapsed.TotalMilliseconds);
                }
                await using var stats = new NpgsqlConnection(connection); await stats.OpenAsync();
                async Task<JsonElement> Plan(string sql, bool card)
                {
                    await using var command = new NpgsqlCommand(sql, stats);
                    if (card) command.Parameters.AddWithValue("id", id); else command.Parameters.AddWithValue("cutoff", cutoff);
                    var json = (string)(await command.ExecuteScalarAsync())!; using var parsed = JsonDocument.Parse(json); return parsed.RootElement.Clone();
                }
                var cardPlan = await Plan("EXPLAIN (ANALYZE,BUFFERS,FORMAT JSON) SELECT \"Kind\",\"Id\",\"Json\",\"DueAt\" FROM \"RuntimeLearningRows\" WHERE \"Kind\"='Learning' AND \"Id\"=@id", true);
                var pagePlan = await Plan("EXPLAIN (ANALYZE,BUFFERS,FORMAT JSON) SELECT \"Kind\",\"Id\",\"Json\",\"DueAt\" FROM \"RuntimeLearningRows\" WHERE \"Kind\"='Learning' AND \"DueAt\"<=@cutoff ORDER BY \"DueAt\",\"Id\" LIMIT 6", false);
                Assert.Contains("Index", cardPlan.GetRawText(), StringComparison.Ordinal);
                Assert.Contains("Index", pagePlan.GetRawText(), StringComparison.Ordinal);
                double P95(List<double> values) => values.Order().ElementAt(47);
                reports.Add(new { cards = count, dueCards = 10, pageSize = 5, pageCandidates = 6, samples = 50, warmup = 5,
                    cardP95Ms = P95(cards), duePageP95Ms = P95(pages), cardPlan, pagePlan });
            } finally { using var pool = new NpgsqlConnection(connection); NpgsqlConnection.ClearPool(pool); }
        }
        await File.WriteAllTextAsync(Environment.GetEnvironmentVariable("JARVIS_PERF_OUTPUT")!, JsonSerializer.Serialize(new {
            generatedAt = DateTimeOffset.UtcNow, synthetic = true, backgroundWorkers = false, pooled = true,
            boundary = "Existing PostgreSQL runtime reader; async card and due-page reads. No provider/hardware calls; not a mutation benchmark.", reports
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
