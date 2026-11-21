using System.Text.Json.Serialization;
using Jarvis.Contracts;

namespace Jarvis.Orchestrator.Forge;

/// <summary>Candidates for one day plus how many were left out by the allocator's candidate limit.</summary>
[JsonConverter(typeof(PrivateCapacityJsonConverter<ForgeCandidateSet>))]
public sealed record ForgeCandidateSet(IReadOnlyList<ForgePlanCandidate> Candidates, int Omitted);

/// <summary>
/// PF-08 candidate adapter: a pure function from existing canonical JARVIS records to allocator candidates.
/// It reads already-materialised learning items and goals (the caller resolves them inside its own boundary),
/// creates no store, history or identity, and never decides capacity. Keys reuse existing identities:
/// <c>review.&lt;domain&gt;</c> for due cards grouped by the existing LearningDomain, <c>goal.&lt;id&gt;</c> for goals.
/// Only active goals tagged with a Forge division are included; untagged goals stay JARVIS-only.
/// Default session lengths are editable starting points derived from Addendum V2 §3 cadences, not obligations.
/// </summary>
public static class ForgePlanCandidates
{
    public const int ReviewMinutesPerCard = 2, MaxReviewBlockMinutes = 45, MinReviewBlockMinutes = 5;

    public static IReadOnlyDictionary<string, int> DefaultSessionMinutes { get; } = new Dictionary<string, int>(StringComparer.Ordinal) {
        ["forge.tech"] = 60, ["forge.career"] = 45, ["forge.business"] = 30, ["forge.wealth"] = 15, ["forge.body"] = 45,
        ["forge.health"] = 10, ["forge.mind"] = 15, ["forge.voice"] = 30, ["forge.presence"] = 10, ["forge.social"] = 20,
        ["forge.relationships"] = 15, ["forge.intellect"] = 30, ["forge.influence"] = 45, ["forge.mentorship"] = 30,
        ["forge.global"] = 30, ["forge.lifestyle"] = 30
    };

    /// <param name="dayEnd">The instant the local plan day ends; cards due by then and goals whose review is due by then qualify.</param>
    public static ForgeCandidateSet Build(DateTimeOffset dayEnd, IEnumerable<LearningItem> learning, IEnumerable<PersonalGoal> goals)
    {
        ArgumentNullException.ThrowIfNull(learning); ArgumentNullException.ThrowIfNull(goals);
        var candidates = new List<ForgePlanCandidate>();

        // One review block per existing learning domain, sized by how many cards are due; earliest due first.
        foreach (var group in learning.Where(x => x.DueAt <= dayEnd).GroupBy(x => x.Domain).OrderBy(g => g.Key)) {
            var minutes = Math.Clamp(group.Count() * ReviewMinutesPerCard, MinReviewBlockMinutes, MaxReviewBlockMinutes);
            candidates.Add(new("review." + group.Key.ToString().ToLowerInvariant(), ForgePlanTaskKind.Review, minutes,
                Math.Min(minutes, MinReviewBlockMinutes), 0, group.Min(x => x.DueAt)));
        }

        // Active, division-tagged goals that are due for attention by the end of the day. Nearer deadlines rank first.
        var goalCandidates = goals
            .Where(g => g.Status == GoalStatus.Active && g.DivisionKey is { } key && ForgeDivisions.IsDivisionKey(key) && g.NextReviewAt <= dayEnd)
            .Select(g => {
                var division = ForgeDivisions.All.Single(d => d.Key == g.DivisionKey);
                var minutes = DefaultSessionMinutes[division.Key];
                var kind = division.Optional ? ForgePlanTaskKind.Optional
                    : division.Key == "forge.career" ? ForgePlanTaskKind.InterviewPractice : ForgePlanTaskKind.Practice;
                var days = (int)Math.Clamp(Math.Floor((g.Deadline - dayEnd).TotalDays), 0, 100);
                return new ForgePlanCandidate("goal." + g.Id.ToString("N"), kind, minutes, Math.Min(minutes, 15), days, g.Deadline);
            })
            .OrderBy(c => c.Priority).ThenBy(c => c.DueBy).ThenBy(c => c.Key, StringComparer.Ordinal);
        candidates.AddRange(goalCandidates);

        // Reviews come first, so a limit never drops due recall in favour of goal work.
        var kept = candidates.Take(ForgeDayAllocator.MaximumCandidates).ToArray();
        return new(Array.AsReadOnly(kept), candidates.Count - kept.Length);
    }
}
