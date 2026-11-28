using System.Security.Cryptography;
using System.Text;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Training;

// Which generated-question rows a canonical update needs, loaded inside its transaction.
public sealed record GeneratedQuestionScope(Guid SessionId, IReadOnlyCollection<Guid> OperationIds, IReadOnlyCollection<Guid> QuestionIds);

// A stored row with the identity/replay data and the Core-supplied provenance and lineage (never shown to users).
// Instances are always detached: nested collections are private read-only copies, so no alias can change a committed row.
public sealed record GeneratedQuestionRecord(GeneratedPracticeQuestion Question, Guid OperationId, string Fingerprint,
    string ProvenanceJson, string LineageJson)
{
    public static GeneratedQuestionRecord Detached(GeneratedQuestionRecord record) => record with
    {
        Question = record.Question with { FollowUps = Array.AsReadOnly(record.Question.FollowUps.ToArray()) }
    };
}

// What a trusted Core writer asks to store. ProvenanceJson and LineageJson come from Core-held context, never tool arguments.
public sealed record GeneratedQuestionWrite(Guid SessionId, Guid OperationId, LearningDomain Topic, string OutlineKey, int OutlineVersion,
    string Fingerprint, string Prompt, string ReferenceOutline, IReadOnlyList<string> FollowUps, string ProvenanceJson, string LineageJson,
    DateTimeOffset CreatedAt);

public static class GeneratedQuestionIds
{
    // Domain-separated, deterministic from (session, operation): the caller never chooses a question ID.
    public static Guid For(Guid sessionId, Guid operationId) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"generated-question-v1\n{sessionId:N}\n{operationId:N}"))[..16]);
}

// The generated-question view of one canonical transaction: rows loaded for the scope, quota counts read under the same lock,
// and the adds/purges to write in the same save. Enforces the writer-boundary rules so no store implementation can skip them.
public sealed class GeneratedQuestionUnit
{
    public const int MaximumPerSession = 200, MaximumTotal = 5000;
    public const int MaximumPrompt = 1200, MaximumReference = 2000, MaximumFollowUps = 2, MaximumFollowUp = 300, MaximumMetadata = 4000;
    private static readonly IReadOnlySet<Guid> AuthoredIds = QuestionBank.All.Select(q => q.Id).ToHashSet();

    private readonly Guid sessionId;
    private readonly IReadOnlySet<Guid> operationIds;
    private readonly Dictionary<Guid, GeneratedQuestionRecord> byId;
    private readonly DateTimeOffset[] sessionCreated;
    private readonly int total;
    private readonly List<(Guid Id, GeneratedQuestionWrite Write)> adds = [];
    private readonly Dictionary<Guid, DateTimeOffset> purges = [];

    // Everything is copied on entry (scope collections, loaded rows, timestamps), so later changes to the caller's objects
    // cannot widen the allowed set or alter what the transaction loaded.
    public GeneratedQuestionUnit(GeneratedQuestionScope scope, IEnumerable<GeneratedQuestionRecord> loaded, IReadOnlyList<DateTimeOffset> sessionCreated, int total)
    {
        ArgumentNullException.ThrowIfNull(scope);
        sessionId = scope.SessionId; operationIds = scope.OperationIds.ToHashSet();
        this.sessionCreated = sessionCreated.ToArray(); this.total = total;
        byId = loaded.Select(GeneratedQuestionRecord.Detached).ToDictionary(r => r.Question.Id);
    }

    public GeneratedQuestionRecord? ById(Guid id) => byId.GetValueOrDefault(id);
    public GeneratedQuestionRecord? ByOperation(Guid operationId) => byId.Values.FirstOrDefault(r => r.OperationId == operationId);
    public int SessionCount => sessionCreated.Length + adds.Count;
    public int SessionCountSince(DateTimeOffset since) => sessionCreated.Count(at => at >= since) + adds.Count(a => a.Write.CreatedAt >= since);

    // Read-only snapshots of the staged changes; the staging collections themselves are never exposed.
    public IReadOnlyList<(Guid Id, GeneratedQuestionWrite Write)> Adds => adds.ToArray();
    public IReadOnlyDictionary<Guid, DateTimeOffset> Purges => new Dictionary<Guid, DateTimeOffset>(purges);

    public Guid Add(GeneratedQuestionWrite input)
    {
        ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(input.FollowUps);
        // Freeze the write before validating it: the accepted payload can't change through the caller's list afterwards.
        var write = input with { FollowUps = Array.AsReadOnly(input.FollowUps.ToArray()) };
        Guard.Require(write.SessionId == sessionId && write.SessionId != Guid.Empty && operationIds.Contains(write.OperationId) &&
            write.OperationId != Guid.Empty, 409, "Generated question is outside this update's scope.");
        var id = GeneratedQuestionIds.For(write.SessionId, write.OperationId);
        Guard.Require(!AuthoredIds.Contains(id), 409, "Generated question identity conflicts with authored content.");
        Guard.Require(ByOperation(write.OperationId) is null && !byId.ContainsKey(id) && adds.All(a => a.Id != id), 409,
            "This practice question was already generated.");
        Guard.Require(TrainingDrillService.Topics.Contains(write.Topic), 400, "Generated questions need a supported practice topic.");
        Guard.Require(write.OutlineKey is { Length: > 0 and <= 64 } && write.OutlineVersion > 0, 400, "A valid outline entry is required.");
        Guard.Require(write.Fingerprint is { Length: 64 } && write.Fingerprint.All(Uri.IsHexDigit), 400, "A complete input fingerprint is required.");
        Guard.Require(write.Prompt is { Length: > 0 and <= MaximumPrompt } && !string.IsNullOrWhiteSpace(write.Prompt)
            && write.ReferenceOutline is { Length: <= MaximumReference } && write.FollowUps is { Count: <= MaximumFollowUps }
            && write.FollowUps.All(f => f is { Length: > 0 and <= MaximumFollowUp }), 400, "Generated question text is outside its bounds.");
        Guard.Require(write.ProvenanceJson is { Length: > 1 and <= MaximumMetadata } && write.LineageJson is { Length: > 1 and <= MaximumMetadata },
            400, "Generated question provenance is outside its bounds.");
        Guard.Require(SessionCount < MaximumPerSession && total + adds.Count < MaximumTotal, 409, "The practice question limit has been reached.");
        adds.Add((id, write));
        return id;
    }

    // Purging clears text, fingerprint and lineage but keeps the content-free ID tombstone. Only loaded rows can be purged,
    // so every purge is decided against rows read under this transaction's lock.
    public void Purge(IEnumerable<Guid> ids, DateTimeOffset at)
    {
        foreach (var id in ids)
        {
            Guard.Require(byId.ContainsKey(id) || adds.Any(a => a.Id == id), 409, "Generated question is outside this update's scope.");
            purges.TryAdd(id, at);
        }
    }
}
