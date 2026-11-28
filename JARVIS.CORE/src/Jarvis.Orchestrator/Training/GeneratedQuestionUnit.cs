using System.Security.Cryptography;
using System.Text;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Training;

// Which generated-question rows a canonical update needs, loaded inside its transaction.
public sealed record GeneratedQuestionScope(Guid SessionId, IReadOnlyCollection<Guid> OperationIds, IReadOnlyCollection<Guid> QuestionIds);

// A stored row with the identity/replay data the writer needs (never shown to users).
public sealed record GeneratedQuestionRecord(GeneratedPracticeQuestion Question, Guid OperationId, string Fingerprint);

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

    private readonly GeneratedQuestionScope scope;
    private readonly Dictionary<Guid, GeneratedQuestionRecord> byId;
    private readonly IReadOnlyList<DateTimeOffset> sessionCreated;
    private readonly int total;
    private readonly List<(Guid Id, GeneratedQuestionWrite Write)> adds = [];
    private readonly Dictionary<Guid, DateTimeOffset> purges = [];

    public GeneratedQuestionUnit(GeneratedQuestionScope scope, IEnumerable<GeneratedQuestionRecord> loaded, IReadOnlyList<DateTimeOffset> sessionCreated, int total)
    {
        this.scope = scope; this.sessionCreated = sessionCreated; this.total = total;
        byId = loaded.ToDictionary(r => r.Question.Id);
    }

    public GeneratedQuestionRecord? ById(Guid id) => byId.GetValueOrDefault(id);
    public GeneratedQuestionRecord? ByOperation(Guid operationId) => byId.Values.FirstOrDefault(r => r.OperationId == operationId);
    public int SessionCount => sessionCreated.Count + adds.Count;
    public int SessionCountSince(DateTimeOffset since) => sessionCreated.Count(at => at >= since) + adds.Count(a => a.Write.CreatedAt >= since);

    public IReadOnlyList<(Guid Id, GeneratedQuestionWrite Write)> Adds => adds;
    public IReadOnlyDictionary<Guid, DateTimeOffset> Purges => purges;

    public Guid Add(GeneratedQuestionWrite write)
    {
        ArgumentNullException.ThrowIfNull(write);
        Guard.Require(write.SessionId == scope.SessionId && write.SessionId != Guid.Empty && scope.OperationIds.Contains(write.OperationId) &&
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
