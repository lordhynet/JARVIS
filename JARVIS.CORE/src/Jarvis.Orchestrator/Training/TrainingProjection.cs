using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Training;

public static class TrainingProjection
{
    public static TimeZoneInfo Zone(string? id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(Guard.Text(id, "TimeZone", 100)); }
        catch (TimeZoneNotFoundException) { throw new DomainException(400, "Unknown time zone."); }
        catch (InvalidTimeZoneException) { throw new DomainException(400, "Invalid time zone."); }
    }

    public static DateTimeOffset CalendarDue(DateTimeOffset at, int days, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone).DateTime.AddDays(days);
        // A skipped wall-clock time advances to the next valid hour. For an ambiguous
        // time choose its first occurrence, rather than depending on host defaults.
        while (zone.IsInvalidTime(local)) local = local.AddHours(1);
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    public static LearningItem Manual(LearningItem item, ReviewRating rating, DateTimeOffset at)
    {
        var days = rating switch
        {
            ReviewRating.Again => 10d / 1440,
            ReviewRating.Hard => Math.Max(1, item.Stability * 1.2),
            ReviewRating.Good => Math.Max(1, item.Stability * 2.5),
            _ => Math.Max(4, item.Stability * 3.5)
        };
        days = Math.Min(days, 3650);
        return item with { Stability = days, DueAt = at.AddDays(days),
            Difficulty = Math.Clamp(item.Difficulty + (rating == ReviewRating.Again ? 1 : rating == ReviewRating.Easy ? -.5 : 0), 1, 10),
            StrongStreak = rating is ReviewRating.Again or ReviewRating.Hard ? 0 : item.StrongStreak,
            SchedulingRuleVersion = "manual-interval-v1" };
    }

    public static void Rebuild(RuntimeState state, Guid questionId)
    {
        state.Learning[questionId] = Project(questionId, state.TrainingBaselines[questionId],
            state.TrainingBaselineReviews.GetValueOrDefault(questionId) ?? [], state.Reviews.Values,
            state.TrainingManualSequences, state.TrainingAttempts.Values);
    }

    // One canonical replay for cohesive and future scoped callers. Inputs must be materialized
    // and resource/epoch/revision checked inside the caller's existing transaction boundary.
    public static LearningItem Project(Guid questionId, LearningItem baseline, IReadOnlySet<Guid> includedReviews,
        IEnumerable<LearningReview> reviews, IReadOnlyDictionary<Guid, long> manualSequences, IEnumerable<TrainingAttempt> attempts)
    {
        Guard.Require(baseline.Id == questionId, 409, "Projection baseline belongs to another question.");
        var current = baseline;
        var events = new List<(DateTimeOffset At, long Order, ReviewRating? Rating, TrainingAssessment? Assessment)>();
        events.AddRange(reviews.Where(x => x.Event.LearningItemId == questionId && !includedReviews.Contains(x.RequestId))
            .Select(x => (x.Event.ReviewedAt, manualSequences.GetValueOrDefault(x.RequestId), (ReviewRating?)x.Event.Rating, (TrainingAssessment?)null)));
        events.AddRange(attempts.Where(x => x.QuestionId == questionId && !x.Skipped && x.DeletedAt is null &&
            x.CanonicalAssessment?.ContentScore is not null && x.CanonicalAssessment.TextRevision == x.TextRevision)
            .Select(x => (x.SubmittedAt, x.Sequence, (ReviewRating?)null, x.CanonicalAssessment)));
        foreach (var entry in events.OrderBy(x => x.At).ThenBy(x => x.Order))
        {
            if (entry.Rating is ReviewRating rating) { current = Manual(current, rating, entry.At); continue; }
            var assessment = entry.Assessment!;
            var score = assessment.ContentScore!.Value;
            var streak = score >= 3 ? current.StrongStreak + 1 : 0;
            var days = score < 2 ? 1 : score < 3 ? 3 : streak >= 2 ? 14 : 7;
            current = current with { MasteryScore = score, MasterySource = assessment.Source, StrongStreak = streak, Stability = days,
                DueAt = CalendarDue(entry.At, days, Zone(assessment.TimeZone)), SchedulingRuleVersion = "content-score-v1" };
        }
        return current;
    }

    public static TrainingAttemptSummary Summary(TrainingAttempt attempt) => new(attempt.Id, attempt.SessionId,
        attempt.QuestionId, attempt.Revision, attempt.Skipped, attempt.SubmittedAt,
        attempt.CanonicalAssessment?.ContentScore, attempt.CanonicalAssessment?.Source ?? "unassessed");
}
