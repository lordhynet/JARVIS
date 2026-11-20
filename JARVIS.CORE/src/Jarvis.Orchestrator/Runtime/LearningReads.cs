using Jarvis.Contracts;

namespace Jarvis.Orchestrator.Runtime;

// Internal index traversal, not an API cursor or a canonical scheduling order.
public sealed record LearningDueCursor(long Epoch, DateTimeOffset Cutoff, DateTimeOffset IndexedDueAt, Guid Id);
public sealed record LearningDuePage(IReadOnlyList<LearningItem> Items, LearningDueCursor? Next, int CandidatesRead);

public static class LearningReadProjection
{
    public const int MaximumPageSize = 100;
    public static DateTimeOffset IndexFloor(DateTimeOffset value) => new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);
    public static DateTimeOffset CandidateUpperBound(DateTimeOffset cutoff)
    {
        var ticks = cutoff.UtcTicks; var remainder = ticks % 10;
        if (remainder == 0) return new(ticks, TimeSpan.Zero);
        var delta = 10 - remainder;
        return ticks > DateTimeOffset.MaxValue.UtcTicks - delta ? DateTimeOffset.MaxValue : new(ticks + delta, TimeSpan.Zero);
    }
    public static void Validate(DateTimeOffset cutoff, int size, LearningDueCursor? after, long epoch)
    {
        Guard.Require(size is >= 1 and <= MaximumPageSize, 400, "Learning page size must be between 1 and 100.");
        if (after is null) return;
        Guard.Require(after.Epoch == epoch && after.Cutoff.UtcTicks == cutoff.UtcTicks, 409, "Learning cursor belongs to another privacy epoch or cutoff.");
        Guard.Require(after.IndexedDueAt.Offset == TimeSpan.Zero &&
            (after.IndexedDueAt.UtcTicks % 10 == 0 || after.IndexedDueAt == DateTimeOffset.MaxValue), 400, "Learning cursor index timestamp is invalid.");
    }
    public static LearningDuePage Page(RuntimeState state, DateTimeOffset cutoff, int size, LearningDueCursor? after, long epoch)
    {
        Validate(cutoff, size, after, epoch); var upper = CandidateUpperBound(cutoff);
        var candidates = state.Learning.Values.Select(item => (Item: item, Index: IndexFloor(item.DueAt)))
            .Where(x => x.Index <= upper && (after is null || x.Index > after.IndexedDueAt || x.Index == after.IndexedDueAt && x.Item.Id.CompareTo(after.Id) > 0))
            .OrderBy(x => x.Index).ThenBy(x => x.Item.Id).Take(size + 1).ToArray();
        var scanned = candidates.Take(size).ToArray();
        return new(scanned.Where(x => x.Item.DueAt <= cutoff).Select(x => x.Item).ToArray(),
            candidates.Length > size ? new(epoch, cutoff, scanned[^1].Index, scanned[^1].Item.Id) : null, candidates.Length);
    }
}
