using Jarvis.Contracts;
using Jarvis.Orchestrator.Cqrs.Learning;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Tests;

public sealed class LearningReadProjectionTests
{
    private static readonly DateTimeOffset Now = new DateTimeOffset(2026, 11, 20, 10, 0, 0, TimeSpan.Zero).AddTicks(7);
    private static LearningItem Card(Guid id, DateTimeOffset due) => new(id, LearningDomain.French, "Synthetic prompt", "Synthetic answer", 0, 5, due, Now);

    [Fact]
    public async Task Narrow_defaults_preserve_values_and_sparse_pages_advance_by_scanned_candidates()
    {
        var state = new RuntimeState(); var future = Card(Guid.Parse("00000000-0000-0000-0000-000000000001"), Now.AddTicks(1));
        var due = Card(Guid.Parse("00000000-0000-0000-0000-000000000002"), Now.ToOffset(TimeSpan.FromHours(5.5)));
        state.Learning[future.Id] = future; state.Learning[due.Id] = due;
        IRuntimeStore store = new InMemoryRuntimeStore(); await store.UpdateAsync(s => { s.Learning = state.Learning; return true; });
        Assert.Equal(due, await store.ReadLearningItemAsync(due.Id)); Assert.Null(await store.ReadLearningItemAsync(Guid.NewGuid()));
        Assert.Equal(due, Assert.Single(await store.ReadLearningItemsAsync(true, Now)));
        var first = await store.ReadDueLearningPageAsync(Now, 1);
        Assert.Empty(first.Items); Assert.NotNull(first.Next); Assert.Equal(2, first.CandidatesRead);
        var second = await store.ReadDueLearningPageAsync(Now, 1, first.Next);
        Assert.Equal(due, Assert.Single(second.Items)); Assert.Null(second.Next); Assert.Equal(1, second.CandidatesRead);
        Assert.Equal(TimeSpan.FromHours(5.5), second.Items[0].DueAt.Offset);
    }

    [Fact]
    public async Task Memory_privacy_epoch_and_cutoff_are_bound_to_internal_cursor()
    {
        var boundary = new MemoryPrivacyBoundary(); var inner = new InMemoryRuntimeStore(); IRuntimeStore store = new GuardedMemoryRuntime(inner, boundary);
        await store.UpdateAsync(s => { for (var i = 0; i < 3; i++) { var card = Card(Guid.NewGuid(), Now); s.Learning[card.Id] = card; } return true; });
        var first = await store.ReadDueLearningPageAsync(Now, 1); Assert.NotNull(first.Next);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => store.ReadDueLearningPageAsync(Now.AddTicks(1), 1, first.Next))).StatusCode);
        boundary.Status = boundary.Status with { Epoch = boundary.Status.Epoch + 1 };
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => store.ReadDueLearningPageAsync(Now, 1, first.Next))).StatusCode);
        var fresh = await store.ReadDueLearningPageAsync(Now, 1); Assert.Equal(boundary.Status.Epoch, fresh.Next!.Epoch);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Invalid_page_sizes_fail_instead_of_fetching_unbounded_history(int size) =>
        Assert.Equal(400, Assert.Throws<DomainException>(() => LearningReadProjection.Page(new(), Now, size, null, 0)).StatusCode);

    [Fact]
    public void Candidate_bound_handles_precision_extremes_without_overflow()
    {
        Assert.Equal(0, LearningReadProjection.CandidateUpperBound(Now).UtcTicks % 10);
        Assert.True(LearningReadProjection.CandidateUpperBound(Now) >= Now);
        Assert.Equal(DateTimeOffset.MinValue, LearningReadProjection.CandidateUpperBound(DateTimeOffset.MinValue));
        Assert.Equal(DateTimeOffset.MaxValue, LearningReadProjection.CandidateUpperBound(DateTimeOffset.MaxValue));
    }

    private sealed class NarrowReader(LearningItem item) : IRuntimeReader
    {
        public bool DueOnly { get; private set; }
        public Task<IReadOnlyList<LearningItem>> ReadLearningItemsAsync(bool dueOnly, DateTimeOffset cutoff, CancellationToken ct = default) {
            DueOnly = dueOnly; return Task.FromResult<IReadOnlyList<LearningItem>>([item]);
        }
        public Task<T> ReadAsync<T>(Func<RuntimeState, T> read, CancellationToken ct = default) => throw new InvalidOperationException("Whole-state read must not be dispatched.");
    }
    [Fact]
    public async Task Existing_learning_list_query_dispatches_the_narrow_read_port()
    {
        var card = Card(Guid.NewGuid(), Now); var reader = new NarrowReader(card);
        var result = await new LearningQueryHandlers(reader, TimeProvider.System).HandleAsync(new ListLearningItems(true));
        Assert.True(reader.DueOnly); Assert.Equal(card, Assert.Single(result));
    }
}
