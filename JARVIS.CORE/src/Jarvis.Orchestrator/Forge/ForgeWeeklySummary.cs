using Jarvis.Contracts;
using Jarvis.Orchestrator.Coaching;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Training;

namespace Jarvis.Orchestrator.Forge;

/// <summary>
/// J4: a pure weekly projection over existing canonical records (learning reviews, training answers, coaching answers,
/// tagged goals). It copies no content, creates no history and makes no provider call. Each record counts once, in the
/// division its existing type maps to; deleted and skipped records never count. Missing data stays unknown.
/// </summary>
public static class ForgeWeeklySummary
{
    // Existing learning domains mapped to the Forge division they evidence.
    private static readonly IReadOnlyDictionary<LearningDomain, string> ReviewDivision = new Dictionary<LearningDomain, string> {
        [LearningDomain.LeetCode] = "forge.tech", [LearningDomain.SystemDesign] = "forge.tech", [LearningDomain.CSharpDotNet] = "forge.tech",
        [LearningDomain.DistributedSystems] = "forge.tech", [LearningDomain.Databases] = "forge.tech", [LearningDomain.ApiSecurity] = "forge.tech",
        [LearningDomain.ProductionDevOps] = "forge.tech", [LearningDomain.PaymentsFintech] = "forge.tech",
        [LearningDomain.French] = "forge.voice", [LearningDomain.Communication] = "forge.voice",
        [LearningDomain.InterviewStory] = "forge.career"
    };

    /// <param name="from">Start of the window (inclusive instant).</param><param name="to">End of the window (exclusive instant).</param>
    public static ForgeWeek Build(DateTimeOffset from, DateTimeOffset to, DateOnly fromDate, DateOnly toDate,
        IEnumerable<LearningReview> reviews, IEnumerable<TrainingAttempt> training, IEnumerable<CoachingAttempt> coaching, IEnumerable<PersonalGoal> goals,
        DateTimeOffset? observedAt = null)
    {
        ArgumentNullException.ThrowIfNull(reviews); ArgumentNullException.ThrowIfNull(training);
        ArgumentNullException.ThrowIfNull(coaching); ArgumentNullException.ThrowIfNull(goals);
        bool In(DateTimeOffset at) => at >= from && at < to && (observedAt is null || at <= observedAt.Value);
        var reviewDivisions = reviews.Where(r => In(r.Event.ReviewedAt)).Select(r => Division(r.Result.Domain)).ToArray();
        var trainingDivisions = training.Where(a => a.DeletedAt is null && !a.Skipped && a.Answers.Count > 0 && In(a.SubmittedAt))
            .Select(a => QuestionBank.All.FirstOrDefault(q => q.Id == a.QuestionId && q.Version == a.QuestionVersion) is { } question
                ? Division(question.Domain) : null).ToArray();
        var coachingDivisions = coaching.Where(a => a.DeletedAt is null && a.Answers.Count > 0 && In(a.CreatedAt)).Select(CoachingDivision).ToArray();
        var reviewCounts = Counts(reviewDivisions); var trainingCounts = Counts(trainingDivisions); var coachingCounts = Counts(coachingDivisions);
        var unclassifiedReviews = reviewDivisions.Count(d => d is null);
        var unclassifiedTraining = trainingDivisions.Count(d => d is null); var unclassifiedCoaching = coachingDivisions.Count(d => d is null);
        var withGoals = goals.Where(g => g.Status == GoalStatus.Active && (observedAt is null || g.CreatedAt <= observedAt.Value) &&
                g.DivisionKey is { } key && ForgeDivisions.IsDivisionKey(key))
            .Select(g => g.DivisionKey!).ToHashSet(StringComparer.Ordinal);

        var divisions = ForgeDivisions.All.Select(d => {
            var r = reviewCounts.GetValueOrDefault(d.Key);
            var t = trainingCounts.GetValueOrDefault(d.Key);
            var c = coachingCounts.GetValueOrDefault(d.Key);
            return new ForgeWeekDivision(d.Key, d.Code, r, t, c, withGoals.Contains(d.Key), r + t + c > 0);
        }).Where(d => d.HasActiveGoal || d.Practised).ToArray();

        var practised = divisions.Count(d => d.Practised);
        decimal? coverage = divisions.Length == 0 || unclassifiedReviews + unclassifiedTraining + unclassifiedCoaching > 0
            ? null : Math.Round((decimal)practised / divisions.Length, 4, MidpointRounding.ToZero);
        return new(fromDate, toDate, reviewDivisions.Length, trainingDivisions.Length, coachingDivisions.Length, divisions.Length, practised, coverage,
            ActualMinutes: null, PlannedMinutes: null, Array.AsReadOnly(divisions), unclassifiedReviews, unclassifiedTraining, unclassifiedCoaching, observedAt);
    }

    private static string? Division(LearningDomain domain) => ReviewDivision.GetValueOrDefault(domain);
    private static Dictionary<string, int> Counts(IEnumerable<string?> divisions) => divisions.Where(d => d is not null)
        .GroupBy(d => d!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
    private static string? CoachingDivision(CoachingAttempt attempt)
    {
        CoachingExercise exercise;
        try { exercise = CoachingCatalog.Get(attempt.ExerciseId); }
        catch (DomainException error) when (error.StatusCode == 404) { return null; }
        if (exercise.Version != attempt.ExerciseVersion) return null;
        return exercise.Area switch { CoachingArea.Communication => "forge.voice", CoachingArea.Interview => "forge.career", _ => null };
    }
}
