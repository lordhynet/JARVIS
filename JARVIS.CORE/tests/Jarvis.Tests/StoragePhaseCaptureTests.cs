using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Jarvis.Orchestrator.Training;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Jarvis.Tests;

internal sealed record StoragePhaseSummary(int Count, double P50Ms, double P95Ms, double MaxMs, double[] SamplesMs);

// Test-only correlation: no operation/resource identity is added to production metric tags.
internal sealed class StoragePhaseCapture : IDisposable
{
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal) {
        "runtime.update.context", "runtime.update.begin", "runtime.update.privacy_gate", "runtime.update.lock_and_load",
        "runtime.update.deserialize", "runtime.update.callback", "runtime.update.serialize_after", "runtime.update.save", "runtime.update.commit",
        "runtime.cold.hydrate", "runtime.learning.hydrate", "runtime.cold.serialize_hash", "runtime.learning.diff",
        "runtime.training.hydrate", "runtime.training.diff" };
    private sealed class Frame(string operation, bool measured)
    {
        private int active = 1;
        public string Operation { get; } = operation;
        public bool Measured { get; } = measured;
        public bool Active => Volatile.Read(ref active) != 0;
        public void Close() => Interlocked.Exchange(ref active, 0);
    }
    private sealed class Scope(Action close) : IDisposable { public void Dispose() => close(); }
    private readonly AsyncLocal<Frame?> current = new();
    private readonly MeterListener listener = new();
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, ConcurrentQueue<double>>> samples = new(StringComparer.Ordinal);
    public StoragePhaseCapture() {
        var target = CoreTelemetry.StoragePhaseDuration;
        listener.InstrumentPublished = (instrument, active) => { if (ReferenceEquals(instrument, target)) active.EnableMeasurementEvents(instrument); };
        listener.SetMeasurementEventCallback<double>((_, seconds, tags, _) => {
            if (current.Value is not { Measured: true, Active: true } frame || !double.IsFinite(seconds) || seconds < 0) return;
            string? phase = null;
            foreach (var tag in tags) if (tag.Key == "phase" && tag.Value is string name) phase = name;
            if (phase is null || !Allowed.Contains(phase)) return;
            samples.GetOrAdd(frame.Operation, _ => new(StringComparer.Ordinal)).GetOrAdd(phase, _ => new()).Enqueue(seconds * 1000);
        });
        listener.Start();
    }
    public IDisposable Begin(string operation, bool measured) {
        if (operation is not ("correction" or "deletion" or "rebuild")) throw new ArgumentOutOfRangeException(nameof(operation));
        var previous = current.Value; var frame = new Frame(operation, measured); current.Value = frame;
        return new Scope(() => { frame.Close(); current.Value = previous; });
    }
    public Dictionary<string, Dictionary<string, StoragePhaseSummary>> Report() => samples.ToDictionary(lane => lane.Key, lane => lane.Value.ToDictionary(phase => phase.Key, phase => {
        var values = phase.Value.ToArray(); var sorted = values.Order().ToArray();
        return new StoragePhaseSummary(values.Length, sorted[(int)Math.Ceiling(sorted.Length * .5) - 1], sorted[(int)Math.Ceiling(sorted.Length * .95) - 1], sorted[^1], values);
    }));
    public void Dispose() => listener.Dispose();
}

public sealed class StoragePhaseCaptureTests
{
    private static void Emit(double seconds, string phase = "runtime.update.callback") =>
        CoreTelemetry.StoragePhaseDuration.Record(seconds, new KeyValuePair<string, object?>("phase", phase));

    [Fact]
    public async Task Phase_capture_ignores_inherited_context_after_its_operation_scope_closes() {
        using var capture = new StoragePhaseCapture(); var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); Task late;
        using (capture.Begin("correction", true)) {
            late = Task.Run(async () => { await finish.Task; Emit(1); }); Emit(.001);
        }
        finish.SetResult(); await late;
        Assert.Equal(1, Assert.Single(capture.Report()["correction"]["runtime.update.callback"].SamplesMs));
    }

    [Fact]
    public async Task Phase_capture_ignores_warmup_unscoped_and_unrelated_work_and_converts_seconds() {
        using var capture = new StoragePhaseCapture(); Emit(1);
        using (capture.Begin("correction", false)) Emit(2);
        using (capture.Begin("correction", true)) {
            Emit(.012);
            using (capture.Begin("deletion", false)) Emit(3);
            Task unrelated;
            using (ExecutionContext.SuppressFlow()) unrelated = Task.Run(() => Emit(4));
            await unrelated;
            await Task.Run(() => Emit(.004, "runtime.cold.hydrate"));
            Emit(.003, "runtime.training.hydrate"); Emit(.001, "runtime.training.diff");
            Emit(.002, "runtime.update.save");
        }
        Emit(5);
        var lane = Assert.Single(capture.Report()).Value;
        Assert.Equal(12, Assert.Single(lane["runtime.update.callback"].SamplesMs));
        Assert.Equal(4, Assert.Single(lane["runtime.cold.hydrate"].SamplesMs));
        Assert.Equal(3, Assert.Single(lane["runtime.training.hydrate"].SamplesMs));
        Assert.Equal(1, Assert.Single(lane["runtime.training.diff"].SamplesMs));
        Assert.Equal(2, Assert.Single(lane["runtime.update.save"].SamplesMs));
    }

    [Fact]
    public async Task Phase_capture_keeps_concurrent_operation_contexts_separate() {
        using var capture = new StoragePhaseCapture();
        async Task Lane(string operation, double seconds) { using var scope = capture.Begin(operation, true); await Task.Yield(); Emit(seconds); }
        await Task.WhenAll(Lane("correction", .001), Lane("deletion", .002));
        var report = capture.Report(); Assert.Equal(2, report.Count);
        Assert.Equal(1, Assert.Single(report["correction"]["runtime.update.callback"].SamplesMs));
        Assert.Equal(2, Assert.Single(report["deletion"]["runtime.update.callback"].SamplesMs));
    }

    [Fact]
    public void Phase_capture_accepts_only_fixed_categories_and_finite_nonnegative_values() {
        using var capture = new StoragePhaseCapture();
        Assert.Throws<ArgumentOutOfRangeException>(() => capture.Begin("arbitrary resource", true));
        using (capture.Begin("rebuild", true)) {
            Emit(1, "unknown resource tag"); Emit(double.NaN); Emit(double.PositiveInfinity); Emit(-1);
            using var other = new Meter("Unrelated"); other.CreateHistogram<double>("jarvis.storage.phase.duration").Record(10);
        }
        Assert.Empty(capture.Report());
    }
}

public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task Storage_phase_profile_keeps_real_transaction_samples_aligned() {
        await using var database = await TestDatabase.CreateAsync(); using var root = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var host = root.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        var fixture = LearningMutationFixture.Make(32, 6); var store = host.Services.GetRequiredService<IRuntimeStore>();
        using var epoch = PrivacyContext.Enter(0); await fixture.SeedSession(host.Services.GetRequiredService<ISessionStore>()); await fixture.Seed(store);
        var service = new TrainingService(store, host.Services.GetRequiredService<ISessionReader>(), new ForgeClock { Now = LearningMutationFixture.At.AddDays(20) });
        using var phases = new StoragePhaseCapture(); await fixture.Exercise(store, service, 1, phases);
        var report = phases.Report(); Assert.Equal(3, report.Count);
        foreach (var lane in report.Values) {
            Assert.Equal(5, lane["runtime.update.commit"].Count);
            Assert.All(lane.Values, phase => Assert.All(phase.SamplesMs, value => Assert.True(double.IsFinite(value) && value >= 0)));
        }
    }
}
