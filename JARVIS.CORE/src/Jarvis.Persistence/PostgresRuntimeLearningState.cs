using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Persistence;

// One existing JARVIS learning/replay history, projected into rows behind the existing callbacks.
internal sealed class PostgresRuntimeLearningState
{
    private static readonly HashSet<string> Properties = [nameof(RuntimeState.Learning), nameof(RuntimeState.Reviews),
        nameof(RuntimeState.TrainingBaselines), nameof(RuntimeState.TrainingBaselineReviews), nameof(RuntimeState.TrainingManualSequences), nameof(RuntimeState.NextTrainingSequence)];
    public static bool IsProperty(string name) => Properties.Contains(name);
    private readonly SessionDbContext db;
    private Dictionary<(string Kind, Guid Id), object>? before;
    private RuntimeLearningControlRow? control;

    public PostgresRuntimeLearningState(SessionDbContext db, RuntimeState state)
    {
        this.db = db;
        state.DeferLearningProperties(() => { (before, control) = Load(state); });
    }
    private (Dictionary<(string Kind, Guid Id), object> Values, RuntimeLearningControlRow Control) Load(RuntimeState state)
    {
        var rows = db.RuntimeLearningRows.AsNoTracking().ToArray();
        if (rows.Any(x => !IsProperty(x.Kind) || x.Kind == nameof(RuntimeState.NextTrainingSequence)))
            throw new InvalidDataException("Unknown persisted learning collection.");
        Dictionary<Guid, T> Read<T>(string kind) => rows.Where(x => x.Kind == kind).ToDictionary(x => x.Id,
            x => JsonSerializer.Deserialize<T>(x.Json) is { } value ? value : throw new InvalidDataException("Null persisted learning record."));
        var learning = Read<LearningItem>(nameof(RuntimeState.Learning));
        var reviews = Read<LearningReview>(nameof(RuntimeState.Reviews));
        var baselines = Read<LearningItem>(nameof(RuntimeState.TrainingBaselines));
        var included = Read<HashSet<Guid>>(nameof(RuntimeState.TrainingBaselineReviews));
        var sequences = Read<long>(nameof(RuntimeState.TrainingManualSequences));
        if (learning.Any(x => x.Key != x.Value.Id) || baselines.Any(x => x.Key != x.Value.Id) || reviews.Any(x => x.Key != x.Value.RequestId))
            throw new InvalidDataException("Learning dictionary and record identities differ.");
        var sequenceControl = db.RuntimeLearningState.Single(x => x.Id == 1);
        state.Learning = learning; state.Reviews = reviews; state.TrainingBaselines = baselines;
        state.TrainingBaselineReviews = included; state.TrainingManualSequences = sequences; state.NextTrainingSequence = sequenceControl.NextTrainingSequence;
        return (Values(state, snapshot: true), sequenceControl);
    }
    private static Dictionary<(string Kind, Guid Id), object> Values(RuntimeState state, bool snapshot = false)
    {
        if (state.Learning.Any(x => x.Key != x.Value.Id) || state.TrainingBaselines.Any(x => x.Key != x.Value.Id) || state.Reviews.Any(x => x.Key != x.Value.RequestId))
            throw new InvalidDataException("Learning dictionary and record identities differ.");
        var result = new Dictionary<(string Kind, Guid Id), object>();
        void Add<T>(string kind, Dictionary<Guid, T> values) {
            foreach (var (id, value) in values) {
                if (value is null) throw new InvalidDataException("Null learning record.");
                result.Add((kind, id), snapshot && value is HashSet<Guid> members ? new HashSet<Guid>(members) : value);
            }
        }
        Add(nameof(RuntimeState.Learning), state.Learning); Add(nameof(RuntimeState.Reviews), state.Reviews);
        Add(nameof(RuntimeState.TrainingBaselines), state.TrainingBaselines); Add(nameof(RuntimeState.TrainingBaselineReviews), state.TrainingBaselineReviews);
        Add(nameof(RuntimeState.TrainingManualSequences), state.TrainingManualSequences);
        return result;
    }
    private static string Json(object value) => JsonSerializer.Serialize(value, value.GetType());
    private static bool Unchanged(object previous, object current)
    {
        // The closed record contracts are immutable. Only membership sets need defensive
        // snapshots. Avoid serializing the whole history to detect one replaced card.
        if (ReferenceEquals(previous, current)) return true;
        if (previous is HashSet<Guid> members && current is HashSet<Guid> next) return members.SetEquals(next);
        if (previous is long sequence && current is long nextSequence) return sequence == nextSequence;
        // Record equality treats differently-offset DateTimeOffsets as equal instants;
        // canonical JSON comparison preserves offset-only provenance changes as well.
        return Json(previous) == Json(current);
    }
    public void Apply(RuntimeState state)
    {
        if (!state.AreLearningPropertiesLoaded) return;
        // Erasure supplies a new empty state; load the previous rows separately so deleted
        // values cannot overwrite that empty state before applying its delta.
        if (before is null) (before, control) = Load(new RuntimeState());
        var after = Values(state);
        foreach (var key in before!.Keys.Except(after.Keys)) db.RuntimeLearningRows.Remove(new() { Kind = key.Kind, Id = key.Id });
        foreach (var (key, value) in after) {
            if (before.TryGetValue(key, out var old) && Unchanged(old, value)) continue;
            var row = new RuntimeLearningRow { Kind = key.Kind, Id = key.Id, Json = Json(value),
                DueAt = key.Kind == nameof(RuntimeState.Learning) ? state.Learning[key.Id].DueAt.ToUniversalTime() : null };
            if (old is null) db.RuntimeLearningRows.Add(row); else db.RuntimeLearningRows.Update(row);
        }
        control!.NextTrainingSequence = state.NextTrainingSequence;
    }
}
