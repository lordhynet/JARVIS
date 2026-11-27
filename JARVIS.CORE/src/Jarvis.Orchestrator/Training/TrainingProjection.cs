using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Training;

// Existing review identity/event/sequence only. Transient projection input, no new history or authority.
public sealed record TrainingReviewInput(Guid RequestId, ReviewEvent Event, long Sequence);

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
        => ProjectEvents(questionId, baseline, includedReviews,
            reviews.Select(r => new TrainingReviewInput(r.RequestId, r.Event, manualSequences.GetValueOrDefault(r.RequestId))), attempts);

    public static LearningItem ProjectEvents(Guid questionId, LearningItem baseline, IReadOnlySet<Guid> includedReviews,
        IEnumerable<TrainingReviewInput> reviews, IEnumerable<TrainingAttempt> attempts)
    {
        Guard.Require(baseline.Id == questionId, 409, "Projection baseline belongs to another question.");
        var current = baseline;
        var events = new List<(DateTimeOffset At, long Order, Guid Identity, ReviewRating? Rating, TrainingAssessment? Assessment)>();
        var selected = reviews.Where(r => r.Event is null || r.Event.LearningItemId == questionId && !includedReviews.Contains(r.RequestId)).ToArray();
        if (selected.Any(r => r.RequestId == Guid.Empty || r.Event is null || r.Event.Id == Guid.Empty ||
            !Enum.IsDefined(r.Event.Rating) || r.Event.ReviewedAt == default || r.Sequence < 0))
            throw new InvalidDataException("Invalid training projection review history.");
        events.AddRange(selected.Select(r => (r.Event.ReviewedAt, r.Sequence, r.RequestId, (ReviewRating?)r.Event.Rating, (TrainingAssessment?)null)));
        events.AddRange(attempts.Where(x => x.QuestionId == questionId && !x.Skipped && x.DeletedAt is null &&
            x.CanonicalAssessment?.ContentScore is not null && x.CanonicalAssessment.TextRevision == x.TextRevision)
            .Select(x => (x.SubmittedAt, x.Sequence, x.Id, (ReviewRating?)null, x.CanonicalAssessment)));
        // Normal sequence order is unchanged. Legacy equal-time/missing-sequence ties must not depend on a SQL plan's row order.
        foreach (var entry in events.OrderBy(x => x.At).ThenBy(x => x.Order).ThenBy(x => x.Identity))
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
