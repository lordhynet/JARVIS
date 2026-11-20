using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Forge;

public enum ForgePlanTaskKind { Review = 1, InterviewPractice, Practice, Study, Optional }

public enum ForgePlanStatus { Planned = 1, CapacityUnknown, ProgrammePaused, NoCapacity }

public enum ForgeDeferReason
{
    CapacityUnknown = 1, ProgrammePaused, NoCapacity, BudgetExhausted, NoOpenWindow, InterviewWithin24Hours
}

/// <summary>
/// One candidate activity for a day. Key is a stable identity (for example a canonical card, goal or drill
/// reference) used only for deterministic ordering and results; it carries no private payload.
/// </summary>
public sealed record ForgePlanCandidate(string Key, ForgePlanTaskKind Kind, int Minutes, int MinimumMinutes, int Priority,
    DateTimeOffset? DueBy = null);

public sealed record ForgePlannedItem(string Key, ForgePlanTaskKind Kind, DateTimeOffset Start, DateTimeOffset End, bool Shortened)
{
    public int Minutes => (int)(End - Start).TotalMinutes;
}

public sealed record ForgeDeferredItem(string Key, ForgePlanTaskKind Kind, ForgeDeferReason Reason);

public sealed record ForgeDayPlan(ForgePlanStatus Status, DateOnly LocalDate, int ProgrammeRevision, int CapacityRevision,
    int? AllocatableMinutes, int PlannedMinutes, IReadOnlyList<ForgePlannedItem> Scheduled, IReadOnlyList<ForgeDeferredItem> Deferred);

/// <summary>
/// PF-08 core: a pure, deterministic day allocator. It reads an already-authorised capacity projection
/// (with JARVIS quiet hours already unioned by <see cref="ForgeCapacityProjection.WithPolicyQuietHours"/>)
/// and places candidates only inside real open windows. It never exceeds allocatable minutes, never overlaps
/// fixed blocks or other items, never invents capacity for unknown/zero/paused days, and gives every
/// unplaced candidate a reason. No storage, clock, provider or calendar write happens here.
/// </summary>
public static class ForgeDayAllocator
{
    public const int MaximumCandidates = 64;
    public const int MaximumTaskMinutes = 600;
    private static readonly TimeSpan InterviewUrgent = TimeSpan.FromHours(24);
    private static readonly TimeSpan InterviewSoon = TimeSpan.FromDays(7);

    /// <param name="notBefore">The earliest instant an item may start (for example "now" when replanning today).</param>
    /// <param name="nextInterviewAt">The next confirmed interview, if any, from the existing interview programme.</param>
    public static ForgeDayPlan Plan(ForgeCapacityProjection capacity, IReadOnlyList<ForgePlanCandidate> candidates,
        DateTimeOffset notBefore, DateTimeOffset? nextInterviewAt = null)
    {
        Guard.Require(capacity is not null, 400, "Capacity is required.");
        var items = Validate(candidates);
        var interviewIn = nextInterviewAt is { } at && at >= notBefore ? at - notBefore : (TimeSpan?)null;
        var ordered = items.OrderBy(x => Rank(x, interviewIn)).ThenBy(x => x.Priority)
            .ThenBy(x => x.DueBy ?? DateTimeOffset.MaxValue).ThenBy(x => x.Key, StringComparer.Ordinal).ToArray();

        ForgeDayPlan Empty(ForgePlanStatus status, ForgeDeferReason reason) => new(status, capacity.LocalDate, capacity.ProgrammeRevision,
            capacity.CapacityRevision, capacity.AllocatableMinutes, 0, [], ordered.Select(x => new ForgeDeferredItem(x.Key, x.Kind, reason)).ToArray());

        if (!capacity.Known) return Empty(ForgePlanStatus.CapacityUnknown, ForgeDeferReason.CapacityUnknown);
        if (capacity.ProgrammePaused) return Empty(ForgePlanStatus.ProgrammePaused, ForgeDeferReason.ProgrammePaused);
        if (!capacity.CanAllocate) return Empty(ForgePlanStatus.NoCapacity, ForgeDeferReason.NoCapacity);

        var budget = capacity.AllocatableMinutes!.Value;
        var windows = OpenWindows(capacity, notBefore);
        var scheduled = new List<ForgePlannedItem>(); var deferred = new List<ForgeDeferredItem>();
        foreach (var item in ordered)
        {
            if (interviewIn <= InterviewUrgent && item.Kind == ForgePlanTaskKind.Optional) {
                deferred.Add(new(item.Key, item.Kind, ForgeDeferReason.InterviewWithin24Hours)); continue;
            }
            if (budget < item.MinimumMinutes) { deferred.Add(new(item.Key, item.Kind, ForgeDeferReason.BudgetExhausted)); continue; }
            // Earliest window that fits the full request within budget; otherwise the earliest that fits the minimum.
            var wanted = Math.Min(item.Minutes, budget);
            var index = windows.FindIndex(w => Length(w) >= wanted);
            var minutes = wanted;
            if (index < 0) {
                index = windows.FindIndex(w => Length(w) >= item.MinimumMinutes);
                if (index < 0) { deferred.Add(new(item.Key, item.Kind, ForgeDeferReason.NoOpenWindow)); continue; }
                minutes = Math.Min(wanted, Length(windows[index]));
            }
            var window = windows[index];
            var end = window.Start.AddMinutes(minutes);
            scheduled.Add(new(item.Key, item.Kind, window.Start, end, minutes < item.Minutes));
            budget -= minutes;
            if (end >= window.End) windows.RemoveAt(index); else windows[index] = (end, window.End);
        }
        var planned = scheduled.Sum(x => x.Minutes);
        return new(ForgePlanStatus.Planned, capacity.LocalDate, capacity.ProgrammeRevision, capacity.CapacityRevision,
            capacity.AllocatableMinutes, planned, scheduled.OrderBy(x => x.Start).ToArray(), deferred);
    }

    // Lower rank goes first. Due reviews always lead (delayed recall is the mastery signal); interview practice
    // moves ahead of other work when an interview is within seven days.
    private static int Rank(ForgePlanCandidate item, TimeSpan? interviewIn) => item.Kind switch {
        ForgePlanTaskKind.Review => 0,
        ForgePlanTaskKind.InterviewPractice when interviewIn <= InterviewSoon => 1,
        ForgePlanTaskKind.Optional => 4,
        _ => 2
    };

    // Same grammar as Domain Registry keys (DomainPolicy.Validate); kept local until the shared helper is merged.
    private static readonly System.Text.RegularExpressions.Regex KeyPattern = new("^[a-z][a-z0-9]*(?:[.-][a-z0-9]+)*$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static bool ValidKey(string? key) => !string.IsNullOrWhiteSpace(key) && key.Length <= 60 && KeyPattern.IsMatch(key);

    private static int Length((DateTimeOffset Start, DateTimeOffset End) window) => (int)(window.End - window.Start).TotalMinutes;

    private static ForgePlanCandidate[] Validate(IReadOnlyList<ForgePlanCandidate> candidates)
    {
        Guard.Require(candidates is not null && candidates.Count <= MaximumCandidates, 400, "Too many plan candidates.");
        var items = candidates!.ToArray();
        foreach (var item in items)
            Guard.Require(item is { } c && ValidKey(c.Key) && Enum.IsDefined(c.Kind) &&
                c.Minutes is >= 1 and <= MaximumTaskMinutes && c.MinimumMinutes >= 1 && c.MinimumMinutes <= c.Minutes &&
                c.Priority is >= 0 and <= 100, 400, "Plan candidate is invalid.");
        Guard.Require(items.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count() == items.Length, 400, "Duplicate plan candidate keys.");
        return items;
    }

    // The local day minus fixed blocks (already unioned and clipped by the projection), minus anything before notBefore.
    // Windows are whole minutes so planned minutes are exact.
    private static List<(DateTimeOffset Start, DateTimeOffset End)> OpenWindows(ForgeCapacityProjection capacity, DateTimeOffset notBefore)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(capacity.TimeZone);
        var localStart = capacity.LocalDate.ToDateTime(TimeOnly.MinValue); var localEnd = capacity.LocalDate.AddDays(1).ToDateTime(TimeOnly.MinValue);
        var dayStart = new DateTimeOffset(localStart, zone.GetUtcOffset(localStart));
        var dayEnd = new DateTimeOffset(localEnd, zone.GetUtcOffset(localEnd));
        var floor = notBefore <= dayStart ? dayStart : CeilingMinute(notBefore);
        var blocked = capacity.FixedBlocks.Select(x => (Start: new DateTimeOffset(x.LocalStart, x.StartOffset), End: new DateTimeOffset(x.LocalEnd, x.EndOffset)))
            .OrderBy(x => x.Start).ToArray();
        var windows = new List<(DateTimeOffset Start, DateTimeOffset End)>();
        var cursor = dayStart;
        foreach (var block in blocked) {
            if (block.Start > cursor) Add(cursor, block.Start);
            if (block.End > cursor) cursor = block.End;
        }
        if (dayEnd > cursor) Add(cursor, dayEnd);
        return windows;

        void Add(DateTimeOffset start, DateTimeOffset end) {
            if (start < floor) start = floor;
            if (end - start >= TimeSpan.FromMinutes(1)) windows.Add((start, end));
        }
    }

    private static DateTimeOffset CeilingMinute(DateTimeOffset value)
    {
        var remainder = value.UtcTicks % TimeSpan.TicksPerMinute;
        return remainder == 0 ? value : value.AddTicks(TimeSpan.TicksPerMinute - remainder);
    }
}
