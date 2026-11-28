using Jarvis.Contracts;

namespace Jarvis.Orchestrator.Training;

// Authored, versioned outline per practice topic. It grounds AI-generated questions: every generated question must name one
// entry, so coverage is steered by these lists rather than by the model. Changing an entry's meaning means a new version.
public static class PracticeOutlines
{
    public const string Version = "practice-outlines-v1";

    private static IReadOnlyList<OutlineEntry> E(params string[] keys) => Array.AsReadOnly(keys.Select(k => new OutlineEntry(k, 1)).ToArray());

    public static IReadOnlyDictionary<LearningDomain, IReadOnlyList<OutlineEntry>> ByTopic { get; } = new Dictionary<LearningDomain, IReadOnlyList<OutlineEntry>>
    {
        [LearningDomain.SystemDesign] = E("requirements", "data-ownership", "consistency", "scaling", "caching", "failure-recovery", "observability"),
        [LearningDomain.DistributedSystems] = E("delivery-guarantees", "idempotency", "ordering", "consensus", "partitions", "backpressure"),
        [LearningDomain.Databases] = E("isolation", "indexing", "schema-evolution", "transactions", "query-plans", "replication"),
        [LearningDomain.ApiSecurity] = E("authentication", "authorization", "input-validation", "secrets", "rate-limiting", "auditing"),
        [LearningDomain.CSharpDotNet] = E("async-await", "dependency-injection", "memory-and-gc", "linq", "error-handling", "testing"),
        [LearningDomain.ProductionDevOps] = E("deployment", "rollback", "monitoring", "incident-response", "capacity", "configuration"),
        [LearningDomain.PaymentsFintech] = E("idempotent-payments", "reconciliation", "ledgers", "fraud", "settlement", "compliance"),
        [LearningDomain.LeetCode] = E("arrays-and-hashing", "two-pointers", "trees-and-graphs", "dynamic-programming", "heaps", "complexity")
    };

    // Authored content comes first: generation is needed only once every authored question on the topic has a retained,
    // non-skipped answer within the window. Fixed authored questions therefore never block new coverage forever.
    public static bool AuthoredExhausted(IEnumerable<TrainingAttempt> attempts, LearningDomain topic, DateTimeOffset now, TimeSpan window)
    {
        var recent = attempts.Where(a => a.DeletedAt is null && !a.Skipped && a.SubmittedAt >= now - window).Select(a => a.QuestionId).ToHashSet();
        return QuestionBank.All.Where(q => q.Domain == topic).All(q => recent.Contains(q.Id));
    }

    // The least-practised outline entry, derived from retained canonical answers to generated questions on this topic.
    // Ties keep the authored outline order, so the choice is deterministic. Null when the topic has no outline.
    public static OutlineEntry? NextEntry(LearningDomain topic, IReadOnlyDictionary<string, int> answeredByEntry)
    {
        if (!ByTopic.TryGetValue(topic, out var entries) || entries.Count == 0) return null;
        return entries.Select((entry, order) => (entry, order, count: answeredByEntry.GetValueOrDefault(entry.Key)))
            .OrderBy(x => x.count).ThenBy(x => x.order).First().entry;
    }
}
