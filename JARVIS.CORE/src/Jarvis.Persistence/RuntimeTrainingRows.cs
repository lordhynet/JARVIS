using System.Collections.ObjectModel;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Persistence;

// Preparation for the approved canonical training unit. Not mapped or used by a runtime writer yet.
// A distinct CLR type prevents cross-table identity aliasing with RuntimeLearningRow.
internal sealed class RuntimeTrainingRow
{
    public string Kind { get; set; } = "";
    public Guid Id { get; set; }
    public string Json { get; set; } = "null";
}

internal readonly record struct RuntimeTrainingKey(string Kind, Guid Id);
internal sealed record RuntimeTrainingChanges(IReadOnlyList<RuntimeTrainingRow> Upserts, IReadOnlyList<RuntimeTrainingKey> Removed);

/// <summary>Complete-unit, closed-kind codec for existing attempts and request receipts. No database I/O or state writes.</summary>
internal sealed class RuntimeTrainingSnapshot
{
    private const string Attempts = nameof(RuntimeState.TrainingAttempts), Operations = nameof(RuntimeState.TrainingOperations);
    private readonly Dictionary<RuntimeTrainingKey, string> before;
    public IReadOnlyDictionary<Guid, TrainingAttempt> TrainingAttempts { get; }
    public IReadOnlyDictionary<Guid, TrainingOperation> TrainingOperations { get; }

    private RuntimeTrainingSnapshot(Dictionary<Guid, TrainingAttempt> attempts, Dictionary<Guid, TrainingOperation> operations)
    {
        before = Capture(attempts, operations); // immutable JSON before-images detect edits to nested mutable lists too
        TrainingAttempts = new ReadOnlyDictionary<Guid, TrainingAttempt>(attempts);
        TrainingOperations = new ReadOnlyDictionary<Guid, TrainingOperation>(operations);
    }

    public static RuntimeTrainingSnapshot Read(IEnumerable<RuntimeTrainingRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var attempts = new Dictionary<Guid, TrainingAttempt>(); var operations = new Dictionary<Guid, TrainingOperation>();
        foreach (var row in rows)
        {
            if (row is null || row.Id == Guid.Empty) throw Invalid();
            switch (row.Kind)
            {
                case Attempts:
                    var attempt = Decode<TrainingAttempt>(row.Json);
                    Validate(row.Id, attempt);
                    if (!attempts.TryAdd(row.Id, attempt)) throw Invalid();
                    break;
                case Operations:
                    var operation = Decode<TrainingOperation>(row.Json);
                    Validate(row.Id, operation);
                    if (!operations.TryAdd(row.Id, operation)) throw Invalid();
                    break;
                default: throw Invalid();
            }
        }
        return new(attempts, operations); // nothing is published until the entire input is valid
    }

    // BOTH dictionaries must describe a complete unit. A scoped row subset must never enter this diff.
    public RuntimeTrainingChanges Prepare(IReadOnlyDictionary<Guid, TrainingAttempt> attempts,
        IReadOnlyDictionary<Guid, TrainingOperation> operations)
    {
        var after = Capture(attempts, operations); // validate the whole after-image before emitting a change plan
        var removed = before.Keys.Where(key => !after.ContainsKey(key)).OrderBy(key => key.Kind, StringComparer.Ordinal).ThenBy(key => key.Id).ToArray();
        var upserts = after.OrderBy(p => p.Key.Kind, StringComparer.Ordinal).ThenBy(p => p.Key.Id)
            .Where(p => !before.TryGetValue(p.Key, out var old) || old != p.Value)
            .Select(p => new RuntimeTrainingRow { Kind = p.Key.Kind, Id = p.Key.Id, Json = p.Value }).ToArray();
        // Unchanged rows are not rewritten, retaining their original JSONB values and unknown fields.
        return new(Array.AsReadOnly(upserts), Array.AsReadOnly(removed));
    }

    private static Dictionary<RuntimeTrainingKey, string> Capture(IReadOnlyDictionary<Guid, TrainingAttempt> attempts,
        IReadOnlyDictionary<Guid, TrainingOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(attempts); ArgumentNullException.ThrowIfNull(operations);
        var values = new Dictionary<RuntimeTrainingKey, string>();
        foreach (var (id, value) in attempts) { Validate(id, value); values.Add(new(Attempts, id), JsonSerializer.Serialize(value)); }
        foreach (var (id, value) in operations) { Validate(id, value); values.Add(new(Operations, id), JsonSerializer.Serialize(value)); }
        return values;
    }
    private static T Decode<T>(string json) where T : class
    {
        if (json is null) throw Invalid();
        try { return JsonSerializer.Deserialize<T>(json) ?? throw Invalid(); }
        catch (JsonException) { throw Invalid(); } // fixed refusal; do not echo a stored answer in an error
    }
    private static void Validate(Guid id, TrainingAttempt? value)
    {
        if (id == Guid.Empty || value is null || id != value.Id || value.SessionId == Guid.Empty || value.QuestionId == Guid.Empty ||
            !Enum.IsDefined(value.Mode) || value.Answers is null || value.Assessments is null || value.Answers.Any(a => a is null) ||
            value.Assessments.Any(a => a is null || a.Scores is null || a.Scores.Any(s => s is null))) throw Invalid();
    }
    private static void Validate(Guid id, TrainingOperation? value)
    {
        // The physical key is RequestId, NOT AttemptId. Null/empty fingerprints are historical purge markers.
        // Do not require a live target attempt: retained receipts may outlive selected content.
        if (id == Guid.Empty || value is null || value.AttemptId == Guid.Empty || string.IsNullOrWhiteSpace(value.Kind)) throw Invalid();
    }
    private static InvalidDataException Invalid() => new("Invalid canonical training row.");
}
