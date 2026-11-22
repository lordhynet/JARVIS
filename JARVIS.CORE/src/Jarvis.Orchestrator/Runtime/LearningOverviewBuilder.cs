using Jarvis.Contracts;

namespace Jarvis.Orchestrator.Runtime;

/// <summary>
/// Pure, read-only projection of learning cards and review events into an overview. No AI and nothing stored.
/// Focus is the domain with the most "Again" ratings in the last seven days (ties: most due now, then name);
/// it stays null when nothing was forgotten, so JARVIS never invents a weakness.
/// </summary>
public static class LearningOverviewBuilder
{
    public const int ForecastDays = 7;

    public static LearningOverview Build(IEnumerable<LearningItem> items, IEnumerable<ReviewEvent> reviews, DateTimeOffset now)
    {
        var cards = items.ToDictionary(i => i.Id);
        var since = now.AddDays(-7);
        var recent = reviews.Where(r => r.ReviewedAt > since && r.ReviewedAt <= now && cards.ContainsKey(r.LearningItemId)).ToArray();
        var forecast = new int[ForecastDays];
        foreach (var card in cards.Values)
        {
            var bucket = card.DueAt <= now ? 0 : (int)Math.Floor((card.DueAt - now).TotalHours / 24);
            if (bucket < ForecastDays) forecast[bucket]++;
        }
        var domains = cards.Values.GroupBy(c => c.Domain).Select(group =>
        {
            var reviewed = recent.Where(r => cards[r.LearningItemId].Domain == group.Key).ToArray();
            var again = reviewed.Count(r => r.Rating == ReviewRating.Again);
            return new LearningDomainOverview(group.Key, group.Count(), group.Count(c => c.DueAt <= now),
                group.Count(c => c.DueAt < now.AddDays(ForecastDays)), reviewed.Length, again, Rate(reviewed.Length, again));
        }).OrderByDescending(d => d.DueNow).ThenByDescending(d => d.Again7Days).ThenBy(d => d.Domain.ToString(), StringComparer.Ordinal).ToArray();
        var focus = domains.Where(d => d.Again7Days > 0).OrderByDescending(d => d.Again7Days).ThenByDescending(d => d.DueNow)
            .ThenBy(d => d.Domain.ToString(), StringComparer.Ordinal).Select(d => (LearningDomain?)d.Domain).FirstOrDefault();
        int Count(ReviewRating rating) => recent.Count(r => r.Rating == rating);
        var againTotal = Count(ReviewRating.Again);
        return new(now, cards.Count, cards.Values.Count(c => c.DueAt <= now), forecast, recent.Length, againTotal,
            Count(ReviewRating.Hard), Count(ReviewRating.Good), Count(ReviewRating.Easy), Rate(recent.Length, againTotal), focus, domains);
    }

    private static double? Rate(int reviewed, int again) => reviewed == 0 ? null : Math.Round((reviewed - again) / (double)reviewed, 3);
}
