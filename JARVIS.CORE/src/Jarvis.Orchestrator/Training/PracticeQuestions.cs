using Jarvis.Contracts;

namespace Jarvis.Orchestrator.Training;

public enum PracticeQuestionOrigin { Authored = 1, Generated }

// A practice question with where it came from. Generated questions are never verified in this design.
public sealed record PracticeQuestion(TrainingQuestion Question, PracticeQuestionOrigin Origin, bool Verified);

// Result of resolving a question ID: available, purged (text removed, ID tombstone kept) or unknown.
public sealed record PracticeQuestionLookup(PracticeQuestion? Question, bool Purged)
{
    public static readonly PracticeQuestionLookup Unknown = new(null, false);
    public static readonly PracticeQuestionLookup Removed = new(null, true);
}

// Session-private generated question as stored. Text is null once purged.
public sealed record GeneratedPracticeQuestion(Guid Id, Guid SessionId, LearningDomain Topic, string OutlineKey, int OutlineVersion,
    int Version, string? Prompt, string? ReferenceOutline, IReadOnlyList<string> FollowUps, DateTimeOffset CreatedAt, DateTimeOffset? PurgedAt);

// Read-only port. Slice 2a has no writer: nothing in the application creates generated questions yet.
public interface IGeneratedPracticeQuestionReader
{
    Task<GeneratedPracticeQuestion?> GetAsync(Guid sessionId, Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<GeneratedPracticeQuestion>> ListAsync(Guid sessionId, CancellationToken ct = default);
}

// In-memory storage has no generated-question writer, so it always reads empty.
public sealed class EmptyGeneratedPracticeQuestionReader : IGeneratedPracticeQuestionReader
{
    public Task<GeneratedPracticeQuestion?> GetAsync(Guid sessionId, Guid id, CancellationToken ct = default) => Task.FromResult<GeneratedPracticeQuestion?>(null);
    public Task<IReadOnlyList<GeneratedPracticeQuestion>> ListAsync(Guid sessionId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<GeneratedPracticeQuestion>>([]);
}

// The single question resolver over authored definitions and session-scoped generated rows. Authored IDs always win and
// behave exactly as QuestionBank today. Generated rows resolve only within their own session; a purged row is "removed",
// never inferred authored or verified. Not yet used by any consumer (slice 2a scaffolding).
public sealed class PracticeQuestionResolver(IGeneratedPracticeQuestionReader generated)
{
    private static readonly IReadOnlySet<Guid> AuthoredIds = QuestionBank.All.Select(q => q.Id).ToHashSet();

    public async Task<PracticeQuestionLookup> ResolveAsync(Guid sessionId, Guid id, CancellationToken ct = default)
    {
        if (QuestionBank.All.FirstOrDefault(q => q.Id == id) is { } authored) return new(new(authored, PracticeQuestionOrigin.Authored, true), false);
        if (sessionId == Guid.Empty) return PracticeQuestionLookup.Unknown;
        var row = await generated.GetAsync(sessionId, id, ct);
        if (row is null || row.SessionId != sessionId || row.Id != id) return PracticeQuestionLookup.Unknown;
        return row.PurgedAt is not null || row.Prompt is null ? PracticeQuestionLookup.Removed : new(new(ToQuestion(row), PracticeQuestionOrigin.Generated, false), false);
    }

    public async Task<IReadOnlyList<PracticeQuestion>> ListAsync(Guid sessionId, LearningDomain? topic = null, CancellationToken ct = default)
    {
        var authored = QuestionBank.All.Where(q => topic is null || q.Domain == topic)
            .Select(q => new PracticeQuestion(q, PracticeQuestionOrigin.Authored, true));
        if (sessionId == Guid.Empty) return authored.ToList();
        var rows = await generated.ListAsync(sessionId, ct);
        // Same precedence as ResolveAsync: an authored ID always wins, so a colliding generated row is never listed (whatever
        // the topic filter). Duplicate generated IDs keep only the earliest row, so every listed ID has one canonical identity.
        var available = rows.Where(r => r.SessionId == sessionId && r.PurgedAt is null && r.Prompt is not null && !AuthoredIds.Contains(r.Id))
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).DistinctBy(r => r.Id)
            .Where(r => topic is null || r.Topic == topic)
            .Select(r => new PracticeQuestion(ToQuestion(r), PracticeQuestionOrigin.Generated, false));
        return authored.Concat(available).ToList();
    }

    // Generated questions have no authored source URL; the reference is the model's outline, labelled unverified by origin.
    private static TrainingQuestion ToQuestion(GeneratedPracticeQuestion row) =>
        new(row.Id, row.Version, row.Topic, row.OutlineKey, row.Prompt!, row.ReferenceOutline ?? "", row.FollowUps, "");
}
