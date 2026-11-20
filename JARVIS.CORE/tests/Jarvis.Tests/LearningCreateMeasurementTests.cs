using System.Diagnostics;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    [RuntimeMeasurementFact]
    public async Task Measure_scoped_creation_against_matching_callback_workload()
    {
        var reports = new List<object>(); var clock = new ForgeClock();
        foreach (var count in new[] { 1_000, 2_000, 10_000 }) {
            var fixture = new RuntimeState();
            for (var i = 0; i < count; i++) {
                var id = Guid.NewGuid(); var card = new LearningItem(id, LearningDomain.French, "Synthetic existing card", "Synthetic answer", 2, 5, clock.Now, clock.Now);
                fixture.Learning.Add(id, card); var request = Guid.NewGuid();
                fixture.Reviews.Add(request, new(request, new(Guid.NewGuid(), id, ReviewRating.Again, TimeSpan.FromSeconds(2), clock.Now), card));
            }
            for (var i = 0; i < 200; i++) {
                var id = Guid.NewGuid(); fixture.Goals[id] = new(id, 1, "Synthetic goal", "Synthetic brief", "Daily", false, clock.Now, clock.Now.AddMonths(1), "active", clock.Now, clock.Now);
            }
            foreach (var scoped in new[] { false, true }) {
                await using var database = await TestDatabase.CreateAsync();
                var connection = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Pooling = true }.ConnectionString;
                try {
                    using var original = new PostgresApiFactory(connection, clock, manualAgents: true);
                    using var host = original.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
                    var store = host.Services.GetRequiredService<IRuntimeStore>(); await SeedReviewFixture(store, fixture);
                    await store.UpdateAsync(s => { s.Goals = fixture.Goals; return true; });
                    var service = new LearningService(scoped ? store : new CallbackCreationStore(store), clock);
                    var samples = new List<double>(); var ids = new HashSet<Guid>();
                    for (var i = 0; i < 55; i++) {
                        var timer = Stopwatch.StartNew(); var card = await service.CreateAsync(new(LearningDomain.French, " New synthetic card ", " New synthetic answer "), default);
                        if (i >= 5) samples.Add(timer.Elapsed.TotalMilliseconds);
                        Assert.True(ids.Add(card.Id)); Assert.Equal("New synthetic card", card.Prompt); Assert.Equal(clock.Now, card.CreatedAt);
                        Assert.Equal(0, card.Stability); Assert.Equal(5, card.Difficulty);
                    }
                    var final = await store.ReadAsync(s => (Cards: s.Learning.Count, Reviews: s.Reviews.Count, Goals: s.Goals.Count, Sequence: s.NextTrainingSequence));
                    Assert.Equal((count + 55, count, 200, 1L), final);
                    reports.Add(new { path = scoped ? "scoped" : "cohesive-callback", initialCards = count, initialReviews = count,
                        p95Ms = samples.Order().ElementAt(47), samplesMs = samples, finalCards = final.Cards, finalReviews = final.Reviews,
                        finalGoals = final.Goals, finalSequence = final.Sequence, distinctNewIds = ids.Count });
                } finally { using var pool = new NpgsqlConnection(connection); NpgsqlConnection.ClearPool(pool); }
            }
        }
        await File.WriteAllTextAsync(Environment.GetEnvironmentVariable("JARVIS_PERF_OUTPUT")!, JsonSerializer.Serialize(new {
            generatedAt = DateTimeOffset.UtcNow, synthetic = true, backgroundWorkers = false, pooled = true, unrelatedGoals = 200, warmup = 5, samples = 50,
            operation = "Same new learning-card creation, no request ID/retry, separate equally seeded databases; existing validation/defaults and writer lock.", reports
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
