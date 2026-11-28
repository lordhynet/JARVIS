using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Training;

// Applies a change to a personal source and, in the same canonical transaction, purges every generated practice question from
// conversations whose model was shown that source (review R4 item 3). Affected conversations become unverifiable, so they
// cannot generate again from stale context.
public static class GeneratedPracticeTriggers
{
    private const string RetryCode = "generated_lineage_changed";
    private const int Attempts = 3;

    // For changes that may touch several stories at once (import deletion, retention): snapshot story versions before the
    // change, then find every conversation shown a story that was edited or removed by it.
    public static IReadOnlyDictionary<Guid, int> StoryVersions(RuntimeState state) => state.Stories.ToDictionary(p => p.Key, p => p.Value.Version);

    public static IReadOnlySet<Guid> AffectedByStoryChanges(RuntimeState state, IReadOnlyDictionary<Guid, int> before)
    {
        var changed = before.Where(p => !state.Stories.TryGetValue(p.Key, out var story) || story.Version != p.Value).Select(p => p.Key).ToHashSet();
        return changed.Count == 0 ? new HashSet<Guid>() : state.PracticeContextManifests.Values
            .Where(m => m.Sources.Any(s => s.Kind == PracticeLineage.Story && changed.Contains(s.Id))).Select(m => m.SessionId).ToHashSet();
    }

    // Purge runtime copies for the given generated questions and stop those conversations generating from stale context.
    public static void PurgeRuntime(RuntimeState state, IReadOnlySet<Guid> questionIds, IReadOnlySet<Guid> sessions, DateTimeOffset at)
    {
        GeneratedPracticePrivacy.Purge(state, questionIds, at);
        foreach (var session in sessions) PracticeLineage.Record(state, session, [], at, unverifiable: true);
    }

    public static IReadOnlySet<Guid> Affected(RuntimeState state, string kind, Guid sourceId) =>
        state.PracticeContextManifests.Values.Where(m => PracticeLineage.Exposes(m, kind, sourceId)).Select(m => m.SessionId).ToHashSet();

    public static async Task<T> UpdateAsync<T>(IRuntimeStore store, string kind, Guid sourceId, Func<RuntimeState, T> update,
        TimeProvider clock, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(update);
        for (var attempt = 1; ; attempt++)
        {
            // Candidate conversations are read first so their rows can be loaded under the transaction's lock; inside, the
            // set is recomputed and a conversation exposed in between forces a bounded retry instead of a missed purge.
            var candidates = await store.ReadAsync(s => Affected(s, kind, sourceId), ct);
            try
            {
                if (candidates.Count == 0)
                    // Ordinary path (no conversation ever recorded this source): unchanged behaviour plus one in-lock check.
                    return await store.UpdateAsync(s =>
                    {
                        Guard.Require(Affected(s, kind, sourceId).Count == 0, 409, "Practice context changed; retry.", RetryCode);
                        return update(s);
                    }, ct);
                return await store.UpdateWithGeneratedQuestionsAsync(new(candidates.First(), [], [], candidates), (s, unit) =>
                {
                    var affected = Affected(s, kind, sourceId);
                    Guard.Require(affected.IsSubsetOf(candidates), 409, "Practice context changed; retry.", RetryCode);
                    var result = update(s);
                    var at = clock.GetUtcNow();
                    var questionIds = unit.UnpurgedIn(affected).ToHashSet();
                    var eligible = GeneratedPracticePrivacy.Purge(s, questionIds, at);
                    unit.Purge(eligible, at);
                    foreach (var session in affected) PracticeLineage.Record(s, session, [], at, unverifiable: true);
                    return result;
                }, ct);
            }
            catch (DomainException e) when (e.Code == RetryCode && attempt < Attempts) { }
        }
    }
}
