using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Forge;

/// <summary>
/// Uses the same Forge "today" projection for the spoken briefing and the owner's Today view.
/// Read-only: newest live programme, cards due before the next programme-local midnight, runtime
/// goals. Returns null when Forge is disabled or no programme exists. No provider call, no write.
/// </summary>
public sealed class ForgeTodayService(IForgeAvailability feature, IForgeProgrammeReader programmes, IRuntimeReader runtime)
{
    public bool Enabled => feature.Enabled;

    public async Task<ForgeBriefing?> BuildAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var programme = await ProgrammeAsync(ct);
        if (programme is null) return null;
        var endOfDay = Start(programme, LocalDate(programme, now).AddDays(1));
        var learning = await runtime.ReadLearningItemsAsync(true, endOfDay, ct);
        var goals = await runtime.ReadAsync<IReadOnlyCollection<PersonalGoal>>(s => s.Goals.Values.ToArray(), ct);
        return ProjectToday(programme, now, endOfDay, learning, goals);
    }

    /// <summary>
    /// J4: the last seven local days ending today, in the programme's time zone. Reads existing canonical records once
    /// in a single runtime callback. Cold and learning hydration remains a cost of this on-demand view.
    /// </summary>
    public Task<ForgeWeek?> BuildWeekAsync(DateTimeOffset now, CancellationToken ct = default) => BuildSpanAsync(now, 7, ct);

    /// <summary>
    /// PF-09 evening review: today's evidence (the same counting as the weekly summary over one local day) and
    /// tomorrow's top three suggestions. Read-only; no plan, write or provider call.
    /// </summary>
    public async Task<ForgeEvening?> BuildEveningAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var programme = await ProgrammeAsync(ct);
        if (programme is null) return null;
        var tomorrowDate = LocalDate(programme, now).AddDays(1);
        var tomorrowStart = Start(programme, tomorrowDate); var tomorrowEnd = Start(programme, tomorrowDate.AddDays(1));
        // One runtime snapshot supplies both halves. Only minimised count/suggestion DTOs leave the callback.
        return await runtime.ReadAsync(s => new ForgeEvening(ProjectSpan(s, programme, now, 1),
            ProjectToday(programme, tomorrowStart, tomorrowEnd, s.Learning.Values, s.Goals.Values.ToArray()), tomorrowDate), ct);
    }

    private async Task<ForgeWeek?> BuildSpanAsync(DateTimeOffset now, int days, CancellationToken ct)
    {
        var programme = await ProgrammeAsync(ct);
        if (programme is null) return null;
        return await runtime.ReadAsync(s => ProjectSpan(s, programme, now, days), ct);
    }

    private async Task<ForgeProgramme?> ProgrammeAsync(CancellationToken ct) => !feature.Enabled ? null :
        (await programmes.ListAsync(ForgeProgrammePolicy.OwnerScope, ct)).FirstOrDefault();

    private static ForgeWeek ProjectSpan(RuntimeState state, ForgeProgramme programme, DateTimeOffset now, int days)
    {
        var today = LocalDate(programme, now);
        var fromDate = today.AddDays(1 - days);
        return ForgeWeeklySummary.Build(Start(programme, fromDate), Start(programme, today.AddDays(1)), fromDate, today,
            state.Reviews.Values, state.TrainingAttempts.Values, state.CoachingAttempts.Values, state.Goals.Values, now);
    }

    private static ForgeBriefing ProjectToday(ForgeProgramme programme, DateTimeOffset at, DateTimeOffset end,
        IEnumerable<LearningItem> learning, IReadOnlyCollection<PersonalGoal> goals)
    {
        // The indexed candidate read can be inclusive/widened; canonical payload ticks decide the half-open day.
        var due = learning.Where(c => c.DueAt < end).ToArray();
        var candidates = ForgePlanCandidates.Build(end, due, goals.Where(g => g.NextReviewAt < end), at);
        return ForgeBriefingProjection.Build(programme, ForgeProgrammePolicy.View(programme, at).Calendar, candidates, goals, due.Length);
    }

    private static DateOnly LocalDate(ForgeProgramme programme, DateTimeOffset at) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(at, TimeZoneInfo.FindSystemTimeZoneById(programme.TimeZone)).DateTime);
    private static DateTimeOffset Start(ForgeProgramme programme, DateOnly day)
    {
        var local = day.ToDateTime(TimeOnly.MinValue);
        return new(local, TimeZoneInfo.FindSystemTimeZoneById(programme.TimeZone).GetUtcOffset(local));
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
        var scheduled = CurrentPlan(programme, calendar, byKey, plan);
        if (scheduled) {
            // Real plan: earliest placed items first, with the minutes actually placed.
            chosen = plan!.Scheduled.OrderBy(x => x.Start).Where(x => byKey.ContainsKey(x.Key))
                .Select(x => (byKey[x.Key], x.Minutes));
        } else {
            // No capacity-backed plan: suggestions in the allocator's selection order; requested minutes, not a schedule.
            // Build already orders admission with the allocator's exact explicit time/interview context.
            // Re-ranking here would recreate a second policy and undo that decision.
            chosen = candidates.Candidates.Select(c => (c, c.Minutes));
        }
        var top = chosen.Take(TopCount).Select(x => new ForgeBriefingItem(Label(x.Candidate, goalById), x.Minutes, Why(x.Candidate)))
            .ToArray();
        return new("active", calendar.DayNumber, calendar.ProgrammeDays, phase, null, scheduled, top, dueReviews);
    }

    /// <summary>Drops items from the end until the caller's byte budget is met; never drops the day line.</summary>
    public static ForgeBriefing TrimTo(ForgeBriefing briefing, int items) =>
        briefing with { Top = briefing.Top.Take(Math.Max(0, items)).ToArray() };

    private static bool CurrentPlan(ForgeProgramme programme, ForgeCalendarView calendar,
        IReadOnlyDictionary<string, ForgePlanCandidate> candidates, ForgeDayPlan? plan)
    {
        if (plan is not { Status: ForgePlanStatus.Planned, CapacityRevision: > 0 } || plan.LocalDate != calendar.LocalToday ||
            plan.ProgrammeRevision != programme.Revision || plan.AllocatableMinutes is not > 0 || plan.Scheduled.Count == 0 ||
            plan.PlannedMinutes > plan.AllocatableMinutes || plan.Scheduled.Select(p => p.Key).Distinct(StringComparer.Ordinal).Count() != plan.Scheduled.Count)
            return false;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(programme.TimeZone);
        var local = calendar.LocalToday.ToDateTime(TimeOnly.MinValue); var next = local.AddDays(1);
        if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local) || zone.IsInvalidTime(next) || zone.IsAmbiguousTime(next)) return false;
        var start = new DateTimeOffset(local, zone.GetUtcOffset(local)); var end = new DateTimeOffset(next, zone.GetUtcOffset(next));
        var ordered = plan.Scheduled.OrderBy(p => p.Start).ToArray();
        long total = 0;
        foreach (var item in ordered) {
            if (!candidates.TryGetValue(item.Key, out var candidate) || item.Kind != candidate.Kind ||
                item.Start < start || item.End > end || item.End <= item.Start || item.Minutes <= 0 ||
                item.End - item.Start != TimeSpan.FromMinutes(item.Minutes) || item.Minutes < candidate.MinimumMinutes || item.Minutes > candidate.Minutes)
                return false;
            total += item.Minutes;
        }
        for (var i = 1; i < ordered.Length; i++) if (ordered[i].Start < ordered[i - 1].End) return false;
        return total == plan.PlannedMinutes;
    }

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
        // Priority is a private sort proposal, not an exact local-calendar deadline to publish.
        _ => "goal review"
    };

    private static string Clean(string? title)
    {
        var text = string.Concat((title ?? "").Select(c => char.IsControl(c) ? ' ' : c)).Trim();
        if (text.Length == 0) return "goal";
        return text.Length <= GoalLabelLength ? text : text[..GoalLabelLength].TrimEnd() + "…";
    }
}
