using System.Diagnostics;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Persistence;
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
    public async Task Measure_learning_history_against_the_matching_cohesive_callback()
    {
        var reports = new List<object>(); var at = LearningHistoryFixture.At;
        foreach (var count in new[] { 1_000, 2_000, 10_000 }) {
            await using var database = await TestDatabase.CreateAsync();
            var connection = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Pooling = true }.ConnectionString;
            try {
                using var root = new PostgresApiFactory(connection, manualAgents: true);
                using var host = root.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
                var store = host.Services.GetRequiredService<IRuntimeStore>(); var reader = host.Services.GetRequiredService<IRuntimeReader>();
                var target = Guid.NewGuid(); var rows = new List<RuntimeLearningRow>(count * 2);
                for (var i = 0; i < count; i++) {
                    var id = i == 0 ? target : Guid.NewGuid(); var review = LearningHistoryFixture.Review(i < 3 ? target : id, at.AddTicks(i));
                    var card = review.Result with { Id = id };
                    rows.Add(new() { Kind = nameof(RuntimeState.Learning), Id = id, Json = JsonSerializer.Serialize(card), DueAt = card.DueAt });
                    rows.Add(HistoryRow(review));
                }
                await using (var db = await host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>().CreateDbContextAsync()) {
                    db.RuntimeLearningRows.AddRange(rows); await db.SaveChangesAsync();
                    await db.Database.ExecuteSqlRawAsync("ANALYZE \"RuntimeLearningRows\"");
                }
                await store.UpdateAsync(s => { for (var i = 0; i < 200; i++) {
                    var id = Guid.NewGuid(); s.Goals[id] = new(id, 1, "Synthetic goal", "Fixture", "practice-reminder", false,
                        at.AddDays(1), at.AddDays(2), "Active", at, at);
                } return true; });
                var old = new List<double>(); var indexed = new List<double>();
                for (var i = 0; i < 55; i++) {
                    var timer = Stopwatch.StartNew();
                    var baseline = await reader.ReadAsync(s => s.Reviews.Values.Where(x => x.Event.LearningItemId == target)
                        .Select(x => x.Event).OrderByDescending(x => x.ReviewedAt).ToArray());
                    if (i >= 5) old.Add(timer.Elapsed.TotalMilliseconds);
                    timer.Restart(); var result = await reader.ReadLearningHistoryAsync(target);
                    if (i >= 5) indexed.Add(timer.Elapsed.TotalMilliseconds);
                    Assert.Equal(baseline, result); Assert.Equal(3, result.Count);
                }
                await using var stats = new NpgsqlConnection(connection); await stats.OpenAsync();
                await using var command = new NpgsqlCommand("""
                    EXPLAIN (ANALYZE,BUFFERS,FORMAT JSON) SELECT "Kind","Id","Json","DueAt" FROM "RuntimeLearningRows"
                    WHERE "Kind"='Reviews' AND lower("Json"->'Event'->>'LearningItemId')=@key
                    """, stats);
                command.Parameters.AddWithValue("key", target.ToString("D"));
                using var plan = JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!);
                Assert.Contains("IX_RuntimeLearningRows_ReviewCard", plan.RootElement.GetRawText(), StringComparison.Ordinal);
                reports.Add(new { cards = count, reviews = count, goals = 200, selectedReviews = 3, samples = 50, warmup = 5,
                    callbackP95Ms = old.Order().ElementAt(47), indexedP95Ms = indexed.Order().ElementAt(47), queryPlan = plan.RootElement.Clone() });
            } finally { using var pool = new NpgsqlConnection(connection); NpgsqlConnection.ClearPool(pool); }
        }
        await File.WriteAllTextAsync(Environment.GetEnvironmentVariable("JARVIS_PERF_OUTPUT")!, JsonSerializer.Serialize(new {
            generatedAt = DateTimeOffset.UtcNow, synthetic = true, pooled = true, backgroundWorkers = false,
            boundary = "Matched old cohesive callback and indexed review-history read; no mutation/provider/voice-hardware claim.", reports
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
