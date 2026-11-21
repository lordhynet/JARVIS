using Jarvis.Contracts;

namespace Jarvis.Orchestrator.Forge;

/// <summary>
/// J4: a pure weekly projection over existing canonical records (learning reviews, training answers, coaching answers,
/// tagged goals). It copies no content, creates no history and makes no provider call. Each record counts once, in the
/// division its existing type maps to; deleted and skipped records never count. Missing data stays unknown.
/// </summary>
public static class ForgeWeeklySummary
{
    // Existing learning domains mapped to the Forge division they evidence.
    public static IReadOnlyDictionary<LearningDomain, string> ReviewDivision { get; } = new Dictionary<LearningDomain, string> {
        [LearningDomain.LeetCode] = "forge.tech", [LearningDomain.SystemDesign] = "forge.tech", [LearningDomain.CSharpDotNet] = "forge.tech",
        [LearningDomain.DistributedSystems] = "forge.tech", [LearningDomain.Databases] = "forge.tech", [LearningDomain.ApiSecurity] = "forge.tech",
        [LearningDomain.ProductionDevOps] = "forge.tech", [LearningDomain.PaymentsFintech] = "forge.tech",
        [LearningDomain.French] = "forge.voice", [LearningDomain.Communication] = "forge.voice",
        [LearningDomain.InterviewStory] = "forge.career"
    };
    public const string TrainingDivision = "forge.tech", CoachingDivision = "forge.career";

    /// <param name="from">Start of the window (inclusive instant).</param><param name="to">End of the window (exclusive instant).</param>
    public static ForgeWeek Build(DateTimeOffset from, DateTimeOffset to, DateOnly fromDate, DateOnly toDate,
        IEnumerable<LearningReview> reviews, IEnumerable<TrainingAttempt> training, IEnumerable<CoachingAttempt> coaching, IEnumerable<PersonalGoal> goals)
    {
        ArgumentNullException.ThrowIfNull(reviews); ArgumentNullException.ThrowIfNull(training);
        ArgumentNullException.ThrowIfNull(coaching); ArgumentNullException.ThrowIfNull(goals);
        bool In(DateTimeOffset at) => at >= from && at < to;
        var reviewCounts = reviews.Where(r => In(r.Event.ReviewedAt) && ReviewDivision.ContainsKey(r.Result.Domain))
            .GroupBy(r => ReviewDivision[r.Result.Domain]).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var trainingCount = training.Count(a => a.DeletedAt is null && !a.Skipped && In(a.SubmittedAt));
        var coachingCount = coaching.Count(a => a.DeletedAt is null && In(a.CreatedAt));
        var withGoals = goals.Where(g => g.Status == GoalStatus.Active && g.DivisionKey is { } key && ForgeDivisions.IsDivisionKey(key))
            .Select(g => g.DivisionKey!).ToHashSet(StringComparer.Ordinal);

        var divisions = ForgeDivisions.All.Select(d => {
            var r = reviewCounts.GetValueOrDefault(d.Key);
            var t = d.Key == TrainingDivision ? trainingCount : 0;
            var c = d.Key == CoachingDivision ? coachingCount : 0;
            return new ForgeWeekDivision(d.Key, d.Code, r, t, c, withGoals.Contains(d.Key), r + t + c > 0);
        }).Where(d => d.HasActiveGoal || d.Practised).ToArray();

        var practised = divisions.Count(d => d.Practised);
        decimal? coverage = divisions.Length == 0 ? null : Math.Round((decimal)practised / divisions.Length, 4, MidpointRounding.ToZero);
        return new(fromDate, toDate, reviewCounts.Values.Sum(), trainingCount, coachingCount, divisions.Length, practised, coverage,
            ActualMinutes: null, PlannedMinutes: null, Array.AsReadOnly(divisions));
    }
}
