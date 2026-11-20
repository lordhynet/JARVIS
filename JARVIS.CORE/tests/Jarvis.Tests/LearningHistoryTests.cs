using Jarvis.Contracts;
using Jarvis.Orchestrator.Cqrs.Learning;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Tests;

internal static class LearningHistoryFixture
{
    public static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-20T16:00:00Z");
    public static LearningReview Review(Guid card, DateTimeOffset at) {
        var item = new LearningItem(card, LearningDomain.French, "Synthetic history prompt", "Synthetic answer", 0, 5, At, At);
        return new(Guid.NewGuid(), new(Guid.NewGuid(), card, ReviewRating.Good, TimeSpan.FromSeconds(1.25), at, "fixture-history"), item);
    }
}

public sealed class LearningHistoryTests
{
    [Fact]
    public async Task Learning_history_memory_preserves_exact_descending_time_and_missing_card_behavior() {
        IRuntimeStore store = new InMemoryRuntimeStore(); var card = Guid.NewGuid(); var at = LearningHistoryFixture.At;
        var reviews = new[] { LearningHistoryFixture.Review(card, at.AddTicks(1)),
            LearningHistoryFixture.Review(card, at.AddTicks(9).ToOffset(TimeSpan.FromHours(-4))),
            LearningHistoryFixture.Review(card, at.AddTicks(7).ToOffset(TimeSpan.FromHours(5.5))),
            LearningHistoryFixture.Review(Guid.NewGuid(), at.AddDays(1)) };
        await store.UpdateAsync(s => { foreach (var r in reviews) s.Reviews.Add(r.RequestId, r); return true; });
        var result = await store.ReadLearningHistoryAsync(card);
        Assert.Equal(reviews.Take(3).OrderByDescending(x => x.Event.ReviewedAt).Select(x => x.Event), result);
        Assert.Equal(TimeSpan.FromHours(5.5), result[1].ReviewedAt.Offset); Assert.Equal(reviews[2].Event.ReviewedAt.Ticks, result[1].ReviewedAt.Ticks);
        Assert.Empty(await store.ReadLearningHistoryAsync(Guid.NewGuid())); Assert.Empty(await store.ReadLearningHistoryAsync(Guid.Empty));
    }

    [Fact]
    public async Task Learning_history_default_reader_keeps_privacy_epoch_and_cancellation_fences() {
        var boundary = new MemoryPrivacyBoundary(); IRuntimeStore store = new GuardedMemoryRuntime(new InMemoryRuntimeStore(), boundary);
        var card = Guid.NewGuid(); var review = LearningHistoryFixture.Review(card, LearningHistoryFixture.At);
        await store.UpdateAsync(s => { s.Reviews.Add(review.RequestId, review); return true; });
        using (PrivacyContext.Enter(1)) Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => store.ReadLearningHistoryAsync(card))).StatusCode);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.ReadLearningHistoryAsync(card, cancelled.Token));
        Assert.Equal(review.Event, Assert.Single(await store.ReadLearningHistoryAsync(card)));
    }

    [Fact]
    public async Task Learning_history_query_dispatches_the_narrow_port_and_forwards_cancellation() {
        var card = Guid.NewGuid(); var review = LearningHistoryFixture.Review(card, LearningHistoryFixture.At); var reader = new NarrowHistoryReader(review.Event);
        using var cancellation = new CancellationTokenSource();
        var result = await new LearningQueryHandlers(reader, TimeProvider.System).HandleAsync(new GetReviewHistory(card), cancellation.Token);
        Assert.Equal(card, reader.Id); Assert.Equal(cancellation.Token, reader.Cancellation); Assert.Equal(review.Event, Assert.Single(result));
    }
    private sealed class NarrowHistoryReader(ReviewEvent review) : IRuntimeReader
    {
        public Guid Id { get; private set; }
        public CancellationToken Cancellation { get; private set; }
        public Task<IReadOnlyList<ReviewEvent>> ReadLearningHistoryAsync(Guid id, CancellationToken ct = default) {
            Id = id; Cancellation = ct; return Task.FromResult<IReadOnlyList<ReviewEvent>>([review]);
        }
        public Task<T> ReadAsync<T>(Func<RuntimeState, T> read, CancellationToken ct = default) => throw new InvalidOperationException("Whole-state history read must not be dispatched.");
    }
}
