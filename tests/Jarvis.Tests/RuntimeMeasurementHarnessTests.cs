using System.Collections.Concurrent;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Jarvis.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Jarvis.Tests;

public sealed class RuntimeMeasurementFactAttribute : FactAttribute
{
    public RuntimeMeasurementFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JARVIS_PERF_TEST_POSTGRES")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JARVIS_PERF_OUTPUT")))
            Skip = "Run scripts/measure-runtime.ps1 to opt into the isolated performance harness.";
    }
}

/// <summary>Opt-in only. The admin connection must target a loopback PostgreSQL server and the postgres database.</summary>
public sealed class RuntimeMeasurementHarnessTests
{
    private const int SessionCount = 200;
    private const int MessagesPerSession = 500;
    private const int LearningCount = 2_000;
    private const int AgentRunCount = 500;
    private const int RuntimeTranscriptCount = 700;

    [RuntimeMeasurementFact]
    public async Task Generate_synthetic_dataset_and_measure_runtime_hot_paths()
    {
        var admin = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("JARVIS_PERF_TEST_POSTGRES"));
        if (admin.Host is null || !IsLoopback(admin.Host) || !string.Equals(admin.Database, "postgres", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Performance runs require a loopback PostgreSQL connection targeting only the postgres admin database.");
        var outputPath = Path.GetFullPath(Environment.GetEnvironmentVariable("JARVIS_PERF_OUTPUT")!);
        var databaseName = "jarvis_perf_" + Guid.NewGuid().ToString("N");
        await using (var connection = new NpgsqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", connection);
            await command.ExecuteNonQueryAsync();
        }
        var databaseConnection = new NpgsqlConnectionStringBuilder(admin.ConnectionString) { Database = databaseName, Pooling = false }.ConnectionString;
        try
        {
            await MeasureDatabaseAsync(databaseConnection, outputPath);
        }
        finally
        {
            await using var connection = new NpgsqlConnection(admin.ConnectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand($"DROP DATABASE \"{databaseName}\" WITH (FORCE)", connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task MeasureDatabaseAsync(string connection, string outputPath)
    {
        var sql = new SqlCallCounter();
        using var host = new MeasurementApiFactory(connection, sql);
        using var client = host.CreateClient(); // Starts the isolated API and applies its migrations.
        var services = host.Services;
        var runtime = services.GetRequiredService<IRuntimeStore>();
        var sessions = services.GetRequiredService<ISessionStore>();
        var device = services.GetRequiredService<DeviceService>();
        var now = DateTimeOffset.UtcNow;
        const string deviceId = "measurement-phone";
        var registeredDevice = await device.RegisterAsync(new(deviceId, "Synthetic measurement phone", DeviceKind.AndroidCompanion,
            new(true, true, false, false, [])));
        registeredDevice = await device.VerifyAsync(deviceId);
        registeredDevice = await device.HeartbeatAsync(deviceId);

        var syntheticSessions = new List<ConversationSession>(SessionCount);
        for (var s = 0; s < SessionCount; s++)
        {
            var id = Guid.NewGuid();
            var messages = Enumerable.Range(0, MessagesPerSession).Select(m => new ChatMessage(
                Guid.NewGuid(), m % 2 == 0 ? "user" : "assistant",
                $"Synthetic conversation {s}, turn {m}: a representative discussion about planning, practice, and follow-up.",
                now.AddMinutes(-MessagesPerSession + m))).ToArray();
            syntheticSessions.Add(new(id, ActivityType.General, $"Synthetic session {s:D3}", SessionStatus.Active,
                deviceId, "Synthetic benchmark context", now.AddDays(-1), now.AddMinutes(-s), [], Messages: messages));
        }
        await using (var db = await services.GetRequiredService<IDbContextFactory<SessionDbContext>>().CreateDbContextAsync())
        {
            db.Sessions.AddRange(syntheticSessions.Select(SessionRow.FromContract));
            await db.SaveChangesAsync();
        }

        var state = new RuntimeState();
        state.Devices[deviceId] = registeredDevice;
        for (var sessionIndex = 0; sessionIndex < syntheticSessions.Count; sessionIndex++)
        {
            var session = syntheticSessions[sessionIndex];
            var transcriptCount = RuntimeTranscriptCount / SessionCount + (sessionIndex < RuntimeTranscriptCount % SessionCount ? 1 : 0);
            for (var index = 0; index < transcriptCount; index++)
            {
                var id = Guid.NewGuid();
                state.VoiceTurns.Add(id, new(id, session.Id, index % 2 == 0 ? "user" : "assistant",
                    new string((char)('a' + index % 20), 6_800), null, now.AddMinutes(-index)));
            }
        }
        for (var index = 0; index < LearningCount; index++)
        {
            var id = Guid.NewGuid();
            state.Learning.Add(id, new(id, LearningDomain.DistributedSystems, $"Synthetic prompt {index}",
                $"Synthetic answer {index} with a concise explanation of the relevant concept.", 2.5, 5,
                now.AddDays(1), now.AddDays(-30)));
        }
        for (var index = 0; index < AgentRunCount; index++)
        {
            var id = Guid.NewGuid();
            state.AgentRuns.Add(id, new(id, null, $"synthetic-{index}", "practice-reminder", 1, false,
                now.AddDays(-1), now.AddDays(2), "Succeeded", []));
        }
        await using (var db = await services.GetRequiredService<IDbContextFactory<SessionDbContext>>().CreateDbContextAsync())
        {
            var row = await db.RuntimeDocuments.SingleAsync(x => x.Id == 1);
            row.Json = JsonSerializer.Serialize(state);
            await db.SaveChangesAsync();
        }

        var documentBytes = await ReadRuntimeDocumentBytesAsync(services.GetRequiredService<IDbContextFactory<SessionDbContext>>());
        using var meter = new RuntimeMetricCapture();
        meter.Start();
        var lockSampleStart = meter.LockWaits.Count;
        for (var index = 0; index < 12; index++)
            await runtime.UpdateAsync(current => { current.AgentControls = current.AgentControls with { PaidAiPaused = index % 2 == 0 }; return true; });
        var lockSamples = meter.LockWaits.Skip(lockSampleStart).ToArray();
        var byteSamples = meter.RuntimeBytes.ToArray();
        meter.Dispose();

        var audio = services.GetRequiredService<AudioOwnership>();
        var currentSession = syntheticSessions[0];
        await device.HeartbeatAsync(deviceId);
        var lease = await audio.StartLocalAsync(currentSession.Id, deviceId, default);
        var currentSamples = await MeasureAsync(25, () => audio.CurrentAsync(lease, default));
        await audio.ReleaseAsync(lease, default);

        var companion = services.GetRequiredService<Jarvis.Orchestrator.Runtime.CompanionVoiceService>();
        var prepareSamples = new double[10];
        for (var index = 0; index < prepareSamples.Length; index++)
        {
            await device.HeartbeatAsync(deviceId);
            var started = Stopwatch.GetTimestamp();
            await companion.PrepareAsync(new(Guid.NewGuid(), deviceId, true), default);
            prepareSamples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        var repair = services.GetRequiredService<IVoiceRepairStore>();
        var target = syntheticSessions[1]; var voiceId = Guid.NewGuid();
        await sessions.UpdateAsync(target.Id, current => current with
        {
            Messages = [.. current.Messages ?? [], new(voiceId, "user", "Synthetic orphaned voice projection", now, true, 1.0)],
            PendingVoiceProjectionIds = [.. current.PendingVoiceProjectionIds ?? [], voiceId]
        });
        var pendingRepairMs = await MeasureOneAsync(() => repair.RepairTranscriptsAsync(default));
        var idleRepairMs = await MeasureOneAsync(() => repair.RepairTranscriptsAsync(default));

        // Count real EF/Npgsql calls from the idle AgentWorker and RuntimeWorker for one full minute.
        await Task.Delay(TimeSpan.FromSeconds(2));
        var commandStart = sql.Count;
        var idleStarted = Stopwatch.GetTimestamp();
        await Task.Delay(TimeSpan.FromMinutes(1));
        var idleSeconds = Stopwatch.GetElapsedTime(idleStarted).TotalSeconds;
        var idleCalls = sql.Count - commandStart;

        var report = new
        {
            generatedAtUtc = DateTimeOffset.UtcNow,
            database = "randomly named disposable loopback PostgreSQL database",
            dataset = new { sessions = SessionCount, messagesPerSession = MessagesPerSession,
                learningCards = LearningCount, agentRuns = AgentRunCount },
            runtimeDocumentBytes = documentBytes,
            updateAsyncLockWaitMs = new { samples = lockSamples.Length, p50 = Percentile(lockSamples.Select(x => x * 1000), .50), p95 = Percentile(lockSamples.Select(x => x * 1000), .95) },
            runtimeBytesHistogram = new { samples = byteSamples.Length, p50 = Percentile(byteSamples.Select(value => (double)value), .50), p95 = Percentile(byteSamples.Select(value => (double)value), .95) },
            currentAsyncMs = Summary(currentSamples),
            companionPrepareMs = Summary(prepareSamples),
            voiceRepairPendingMs = pendingRepairMs,
            voiceRepairIdleMs = idleRepairMs,
            idleWorkerDbCallsPerMinute = Math.Round(idleCalls * 60.0 / idleSeconds, 2),
            idleWorkerObservationSeconds = Math.Round(idleSeconds, 2)
        };
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Performance report written: {outputPath}");
        Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static bool IsLoopback(string host) => string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);

    private static async Task<long> ReadRuntimeDocumentBytesAsync(IDbContextFactory<SessionDbContext> factory)
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Database.SqlQueryRaw<long>("SELECT octet_length(\"Json\"::text) AS \"Value\" FROM \"RuntimeDocuments\" WHERE \"Id\"=1").SingleAsync();
    }

    private static async Task<double[]> MeasureAsync<T>(int count, Func<Task<T>> operation)
    {
        var samples = new double[count];
        for (var index = 0; index < count; index++)
        {
            var started = Stopwatch.GetTimestamp();
            await operation();
            samples[index] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        return samples;
    }

    private static async Task<double> MeasureOneAsync<T>(Func<Task<T>> operation)
    {
        var started = Stopwatch.GetTimestamp(); await operation(); return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
    }

    private static object Summary(double[] samples) => new
    {
        samples = samples.Length,
        p50 = Percentile(samples, .50),
        p95 = Percentile(samples, .95)
    };

    private static double Percentile(IEnumerable<double> values, double percentile)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0) return 0;
        return Math.Round(sorted[(int)Math.Ceiling(percentile * sorted.Length) - 1], 3);
    }

    private sealed class RuntimeMetricCapture : IDisposable
    {
        private readonly MeterListener listener = new();
        public ConcurrentQueue<double> LockWaits { get; } = new();
        public ConcurrentQueue<long> RuntimeBytes { get; } = new();
        public void Start()
        {
            listener.InstrumentPublished = (instrument, current) =>
            {
                if (instrument.Meter.Name == CoreTelemetry.Name && instrument.Name is "jarvis.runtime.lock_wait" or "jarvis.runtime.bytes")
                    current.EnableMeasurementEvents(instrument);
            };
            listener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
            {
                if (instrument.Name == "jarvis.runtime.lock_wait") LockWaits.Enqueue(value);
            });
            listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
            {
                if (instrument.Name == "jarvis.runtime.bytes") RuntimeBytes.Enqueue(value);
            });
            listener.Start();
        }
        public void Dispose() => listener.Dispose();
    }

    private sealed class SqlCallCounter : DbCommandInterceptor
    {
        private long count;
        public long Count => Interlocked.Read(ref count);
        private void Increment() => Interlocked.Increment(ref count);
        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result) { Increment(); return result; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) { Increment(); return ValueTask.FromResult(result); }
        public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result) { Increment(); return result; }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default) { Increment(); return ValueTask.FromResult(result); }
        public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result) { Increment(); return result; }
        public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default) { Increment(); return ValueTask.FromResult(result); }
    }

    private sealed class CountingContextFactory(string connection, SqlCallCounter counter) : IDbContextFactory<SessionDbContext>
    {
        private readonly DbContextOptions<SessionDbContext> options = new DbContextOptionsBuilder<SessionDbContext>()
            .UseNpgsql(connection).AddInterceptors(counter).Options;
        public SessionDbContext CreateDbContext() => new(options);
        public Task<SessionDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(new SessionDbContext(options));
    }

    private sealed class MeasurementApiFactory(string connection, SqlCallCounter counter) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            TestBudgetSource.Configure(builder);
            builder.UseEnvironment("Testing");
            builder.UseSetting("Jarvis:ApiKey", JarvisApiFactory.TestKey);
            builder.UseSetting("Jarvis:StorageProvider", "Postgres");
            builder.UseSetting("ConnectionStrings:Jarvis", connection);
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IDbContextFactory<SessionDbContext>>();
                services.AddSingleton(counter);
                services.AddSingleton<IDbContextFactory<SessionDbContext>>(_ => new CountingContextFactory(connection, counter));
            });
        }
    }
}
