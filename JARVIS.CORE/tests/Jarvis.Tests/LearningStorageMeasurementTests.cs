using System.Diagnostics;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    [RuntimeMeasurementFact]
    public async Task Measure_shared_learning_storage_with_unrelated_cold_state()
    {
        // Opt-in, synthetic and disposable; the shared gate driver verifies loopback/test-server identity.
        await using var database = await TestDatabase.CreateAsync();
        var connection = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Pooling = true }.ConnectionString;
        try {
            using var baseHost = new PostgresApiFactory(connection, manualAgents: true);
            using var host = baseHost.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.RemoveAll<IHostedService>()));
            using var client = host.CreateClient(); // Start only the isolated API; no maintenance sweep can enter a timed sample.
            var runtime = host.Services.GetRequiredService<IRuntimeStore>(); var now = DateTimeOffset.UtcNow;
            var first = Guid.NewGuid();
            await runtime.UpdateAsync(s => {
                for (var i = 0; i < 1000; i++) {
                    var id = i == 0 ? first : Guid.NewGuid();
                    var card = new LearningItem(id, LearningDomain.CSharpDotNet, "Synthetic retrieval prompt " + i,
                        new string('a', 200), 2, 5, now.AddDays(1), now);
                    s.Learning.Add(id, card);
                    var request = Guid.NewGuid(); s.Reviews.Add(request, new(request, new(Guid.NewGuid(), id, ReviewRating.Good, TimeSpan.FromSeconds(3), now), card));
                }
                for (var i = 0; i < 200; i++) {
                    var id = Guid.NewGuid(); s.Goals.Add(id, new(id, 1, "Synthetic unrelated goal", new string('g', 1900), "practice-reminder", false,
                        now.AddDays(1), now.AddMonths(2), GoalStatus.Paused, now, now));
                }
                return true;
            });
            var samples = new List<double>(); var controls = new List<double>();
            for (var i = 0; i < 55; i++) {
                var timer = Stopwatch.StartNew();
                await runtime.UpdateAsync(s => { s.Learning[first] = s.Learning[first] with { Stability = s.Learning[first].Stability + 1 }; return true; });
                if (i >= 5) samples.Add(timer.Elapsed.TotalMilliseconds);
                timer.Restart();
                await runtime.UpdateAsync(s => { s.AgentControls = s.AgentControls with { PaidAiPaused = !s.AgentControls.EffectivePaidAiPaused }; return true; });
                if (i >= 5) controls.Add(timer.Elapsed.TotalMilliseconds);
            }
            var coldLoaded = await runtime.ReadAsync(s => { _ = s.Learning.Count; return s.AreColdPropertiesLoaded; });
            await using var db = await host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>().CreateDbContextAsync();
            var coldBytes = await db.Database.SqlQueryRaw<long>("SELECT octet_length(\"Json\"::text)::bigint AS \"Value\" FROM \"RuntimeDocuments\" WHERE \"Id\"=1").SingleAsync();
            double P95(List<double> values) => values.Order().ElementAt((int)Math.Ceiling(values.Count * .95) - 1);
            var result = new { learningCards = 1000, reviews = 1000, unrelatedGoals = 200, samples = 50, warmup = 5, pooling = true,
                learningUpdateP95Ms = P95(samples), hotControlP95Ms = P95(controls), coldDocumentBytes = coldBytes,
                readingLearningLoadsColdDocument = coldLoaded, backgroundWorkers = false, synthetic = true, observedAt = DateTimeOffset.UtcNow };
            await File.WriteAllTextAsync(Environment.GetEnvironmentVariable("JARVIS_PERF_OUTPUT")!, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        } finally { using var pool = new NpgsqlConnection(connection); NpgsqlConnection.ClearPool(pool); }
    }
}
