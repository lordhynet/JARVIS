using System.Diagnostics;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Training;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    [RuntimeMeasurementFact]
    public async Task Measure_scoped_review_against_matching_callback_workload()
    {
        var reports = new List<object>(); var clock = new ForgeClock(); var fixture = new RuntimeState();
        for (var i = 0; i < 1000; i++) {
            var id = Guid.NewGuid(); var card = new LearningItem(id, LearningDomain.French, "Synthetic review fixture", "Synthetic answer", 2, 5, clock.Now, clock.Now);
            fixture.Learning.Add(id, card); var request = Guid.NewGuid();
            fixture.Reviews.Add(request, new(request, new(Guid.NewGuid(), id, ReviewRating.Again, TimeSpan.FromSeconds(2), clock.Now), card));
        }
        var target = fixture.Learning.Keys.First(); fixture.TrainingBaselines[target] = fixture.Learning[target];
        fixture.TrainingBaselineReviews[target] = fixture.Reviews.Keys.ToHashSet();
        for (var i = 0; i < 200; i++) {
            var id = Guid.NewGuid(); fixture.Goals[id] = new(id, 1, "Synthetic goal", "Synthetic brief", "Daily", false, clock.Now, clock.Now.AddMonths(1), "active", clock.Now, clock.Now);
        }
        foreach (var scoped in new[] { false, true }) {
            await using var database = await TestDatabase.CreateAsync();
            var connection = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Pooling = true }.ConnectionString;
            try {
                using var original = new PostgresApiFactory(connection, clock, manualAgents: true);
                using var host = original.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
                var store = host.Services.GetRequiredService<IRuntimeStore>();
                await SeedReviewFixture(store, fixture); await store.UpdateAsync(s => { s.Goals = fixture.Goals; return true; });
                var service = new LearningService(scoped ? store : new CallbackReviewStore(store), clock); var samples = new List<double>();
                for (var i = 0; i < 55; i++) {
                    var timer = Stopwatch.StartNew(); var result = await service.ReviewAsync(target, new(Guid.NewGuid(), ReviewRating.Again, 2), default);
                    if (i >= 5) samples.Add(timer.Elapsed.TotalMilliseconds);
                    Assert.Equal(TrainingProjection.Manual(fixture.Learning[target], ReviewRating.Again, clock.Now).DueAt, result.DueAt);
                }
                var final = await store.ReadAsync(s => (Reviews: s.Reviews.Count, Sequence: s.NextTrainingSequence, Manual: s.TrainingManualSequences.Count));
                Assert.Equal((1055, 56L, 55), final);
                reports.Add(new { path = scoped ? "scoped" : "cohesive-callback", p95Ms = samples.Order().ElementAt(47), samplesMs = samples,
                    finalReviews = final.Reviews, finalSequence = final.Sequence, finalManualSequences = final.Manual });
            } finally { using var pool = new NpgsqlConnection(connection); NpgsqlConnection.ClearPool(pool); }
        }
        await File.WriteAllTextAsync(Environment.GetEnvironmentVariable("JARVIS_PERF_OUTPUT")!, JsonSerializer.Serialize(new {
            generatedAt = DateTimeOffset.UtcNow, synthetic = true, backgroundWorkers = false, pooled = true,
            initialCards = 1000, initialReviews = 1000, unrelatedGoals = 200, baselinePresent = true, warmup = 5, samples = 50,
            operation = "Same manual Again review, unique request per call, existing scheduler and shared writer lock; separate equally seeded databases.", reports
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
