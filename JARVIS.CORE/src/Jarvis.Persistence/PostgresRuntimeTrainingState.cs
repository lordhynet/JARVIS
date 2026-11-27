using System.Diagnostics;
using Jarvis.Orchestrator.Runtime;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Persistence;

// Same complete-unit callbacks and D2/root writer transaction, one canonical training authority.
internal sealed class PostgresRuntimeTrainingState
{
    private readonly SessionDbContext db;
    private RuntimeTrainingSnapshot? before;
    public static bool IsProperty(string name) => name is nameof(RuntimeState.TrainingAttempts) or nameof(RuntimeState.TrainingOperations);
    public PostgresRuntimeTrainingState(SessionDbContext db, RuntimeState state)
    {
        this.db = db;
        state.DeferTrainingProperties(() => before = Load(state));
    }
    private RuntimeTrainingSnapshot Load(RuntimeState state)
    {
        var phase = Stopwatch.GetTimestamp();
        var snapshot = RuntimeTrainingSnapshot.Read(db.RuntimeTrainingRows.AsNoTracking().ToArray());
        state.TrainingAttempts = snapshot.TrainingAttempts.ToDictionary();
        state.TrainingOperations = snapshot.TrainingOperations.ToDictionary();
        CoreTelemetry.RecordStoragePhase(phase, "runtime.training.hydrate");
        return snapshot;
    }
    public void Apply(RuntimeState state)
    {
        if (!state.AreTrainingPropertiesLoaded) return;
        var phase = Stopwatch.GetTimestamp();
        // Erasure/new empty state is complete: old rows must never overwrite its after-image.
        before ??= Load(new RuntimeState());
        var changes = before.Prepare(state.TrainingAttempts, state.TrainingOperations);
        foreach (var key in changes.Removed) db.RuntimeTrainingRows.Remove(new() { Kind = key.Kind, Id = key.Id });
        foreach (var row in changes.Upserts)
        {
            var existed = row.Kind == nameof(RuntimeState.TrainingAttempts)
                ? before.TrainingAttempts.ContainsKey(row.Id) : before.TrainingOperations.ContainsKey(row.Id);
            if (existed) db.RuntimeTrainingRows.Update(row); else db.RuntimeTrainingRows.Add(row);
        }
        CoreTelemetry.RecordStoragePhase(phase, "runtime.training.diff");
    }
}
