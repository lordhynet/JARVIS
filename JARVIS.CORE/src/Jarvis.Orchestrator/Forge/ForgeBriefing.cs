using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Forge;

/// <summary>
/// Builds the Forge "today" block once for both the spoken briefing and the owner's browser Today view, so the two
/// can never disagree. Read-only: newest live programme, cards due by the end of the programme's local day, runtime
/// goals. Returns null when Forge is disabled or no programme exists. No provider call, no write.
/// </summary>
public sealed class ForgeTodayService(IForgeAvailability feature, IForgeProgrammeReader programmes, IRuntimeReader runtime)
{
    public bool Enabled => feature.Enabled;

    public async Task<ForgeBriefing?> BuildAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        if (!feature.Enabled) return null;
        var programme = (await programmes.ListAsync(ForgeProgrammePolicy.OwnerScope, ct)).FirstOrDefault();
        if (programme is null) return null;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(programme.TimeZone);
        var local = TimeZoneInfo.ConvertTime(now, zone).Date.AddDays(1);
        var endOfDay = new DateTimeOffset(local, zone.GetUtcOffset(local));
        var calendar = ForgeProgrammePolicy.View(programme, now).Calendar;
        var dueToday = await runtime.ReadLearningItemsAsync(true, endOfDay, ct);
        var goals = await runtime.ReadAsync<IReadOnlyCollection<PersonalGoal>>(s => s.Goals.Values.ToArray(), ct);
        var candidates = ForgePlanCandidates.Build(endOfDay, dueToday, goals, now);
        return ForgeBriefingProjection.Build(programme, calendar, candidates, goals, dueToday.Count);
    }
}

/// <summary>
/// PF-09 core: a pure projection from the programme calendar, the PF-08 candidates and (optionally) a day plan into a
/// small briefing block. No storage, provider call or clock read. Protected divisions (WEALTH, HEALTH, GLOBAL,
/// RELATIONSHIPS) are named by division only, so their goal titles never reach model context or speech.
/// </summary>
public static class ForgeBriefingProjection
{
    public const int TopCount = 3, GoalLabelLength = 40;

    private static readonly IReadOnlyDictionary<LearningDomain, string> ReviewLabels = new Dictionary<LearningDomain, string> {
        [LearningDomain.LeetCode] = "Algorithms review", [LearningDomain.SystemDesign] = "System design review",
        [LearningDomain.French] = "French review", [LearningDomain.InterviewStory] = "Interview story review",
        [LearningDomain.Communication] = "Communication review", [LearningDomain.CSharpDotNet] = "C# and .NET review",
        [LearningDomain.DistributedSystems] = "Distributed systems review", [LearningDomain.Databases] = "Databases review",
        [LearningDomain.ApiSecurity] = "API security review", [LearningDomain.ProductionDevOps] = "Production operations review",
        [LearningDomain.PaymentsFintech] = "Payments review"
    };

    public static ForgeBriefing Build(ForgeProgramme programme, ForgeCalendarView calendar, ForgeCandidateSet candidates,
        IReadOnlyCollection<PersonalGoal> goals, int dueReviews, ForgeDayPlan? plan = null)
    {
        ArgumentNullException.ThrowIfNull(programme); ArgumentNullException.ThrowIfNull(calendar);
        ArgumentNullException.ThrowIfNull(candidates); ArgumentNullException.ThrowIfNull(goals);
        var phase = calendar.Phase?.Title;
        if (calendar.Status == ForgeProgrammeStatus.NotStarted)
            return new("not-started", 0, calendar.ProgrammeDays, null, calendar.DaysUntilStart, false, [], dueReviews);
        if (calendar.Status == ForgeProgrammeStatus.Graduated)
            return new("graduated", calendar.ProgrammeDays, calendar.ProgrammeDays, null, null, false, [], dueReviews);
        if (programme.Paused)
            return new("paused", calendar.DayNumber, calendar.ProgrammeDays, phase, null, false, [], dueReviews);

        var byKey = candidates.Candidates.ToDictionary(c => c.Key, StringComparer.Ordinal);
        var goalById = goals.ToDictionary(g => "goal." + g.Id.ToString("N"), StringComparer.Ordinal);
        IEnumerable<(ForgePlanCandidate Candidate, int Minutes)> chosen;
        var scheduled = plan is { Status: ForgePlanStatus.Planned } && plan.Scheduled.Count > 0;
        if (scheduled) {
            // Real plan: earliest placed items first, with the minutes actually placed.
            chosen = plan!.Scheduled.OrderBy(x => x.Start).Where(x => byKey.ContainsKey(x.Key))
                .Select(x => (byKey[x.Key], x.Minutes));
        } else {
            // No capacity-backed plan: suggestions in the allocator's selection order; requested minutes, not a schedule.
            chosen = candidates.Candidates.OrderBy(c => Rank(c.Kind)).ThenBy(c => c.Priority)
                .ThenBy(c => c.DueBy ?? DateTimeOffset.MaxValue).ThenBy(c => c.Key, StringComparer.Ordinal)
                .Select(c => (c, c.Minutes));
        }
        var top = chosen.Take(TopCount).Select(x => new ForgeBriefingItem(Label(x.Candidate, goalById), x.Minutes, Why(x.Candidate)))
            .ToArray();
        return new("active", calendar.DayNumber, calendar.ProgrammeDays, phase, null, scheduled, top, dueReviews);
    }

    /// <summary>Drops items from the end until the caller's byte budget is met; never drops the day line.</summary>
    public static ForgeBriefing TrimTo(ForgeBriefing briefing, int items) =>
        briefing with { Top = briefing.Top.Take(Math.Max(0, items)).ToArray() };

    private static int Rank(ForgePlanTaskKind kind) => kind switch {
        ForgePlanTaskKind.Review => 0, ForgePlanTaskKind.InterviewPractice => 1, ForgePlanTaskKind.Optional => 4, _ => 2 };

    private static string Label(ForgePlanCandidate candidate, IReadOnlyDictionary<string, PersonalGoal> goals)
    {
        if (candidate.Kind == ForgePlanTaskKind.Review)
            return ReviewLabels.FirstOrDefault(p => candidate.Key == "review." + p.Key.ToString().ToLowerInvariant()).Value ?? "Review";
        if (goals.TryGetValue(candidate.Key, out var goal) && goal.DivisionKey is { } key &&
            ForgeDivisions.All.FirstOrDefault(d => d.Key == key) is { } division) {
            if (division.Protected) return division.Code + " goal"; // never the private title
            return division.Code + ": " + Clean(goal.Title);
        }
        return "Goal";
    }

    private static string Why(ForgePlanCandidate candidate) => candidate.Kind switch {
        ForgePlanTaskKind.Review => "due recall",
        ForgePlanTaskKind.InterviewPractice => "interview practice",
        ForgePlanTaskKind.Optional => "optional",
        _ => candidate.Priority == 0 ? "goal deadline today" : "goal deadline in " + candidate.Priority + " days"
    };

    private static string Clean(string? title)
    {
        var text = string.Concat((title ?? "").Select(c => char.IsControl(c) ? ' ' : c)).Trim();
        if (text.Length == 0) return "goal";
        return text.Length <= GoalLabelLength ? text : text[..GoalLabelLength].TrimEnd() + "…";
    }
}
