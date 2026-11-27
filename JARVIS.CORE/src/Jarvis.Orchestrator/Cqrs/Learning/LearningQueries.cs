using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Cqrs.Learning;

public sealed record ListLearningItems(bool DueOnly = false) : IQuery<IReadOnlyList<LearningItem>>;
public sealed record GetReviewHistory(Guid ItemId) : IQuery<IReadOnlyList<ReviewEvent>>;
public sealed record GetLearningOverview : IQuery<LearningOverview>;

public sealed class LearningQueryHandlers(IRuntimeReader reader, TimeProvider clock) :
    IQueryHandler<ListLearningItems, IReadOnlyList<LearningItem>>, IQueryHandler<GetReviewHistory, IReadOnlyList<ReviewEvent>>,
    IQueryHandler<GetLearningOverview, LearningOverview>
{
    public Task<IReadOnlyList<LearningItem>> HandleAsync(ListLearningItems query, CancellationToken ct = default) =>
        reader.ReadLearningItemsAsync(query.DueOnly, clock.GetUtcNow(), ct);

    public Task<IReadOnlyList<ReviewEvent>> HandleAsync(GetReviewHistory query, CancellationToken ct = default) =>
        reader.ReadLearningHistoryAsync(query.ItemId, ct);

    public Task<LearningOverview> HandleAsync(GetLearningOverview query, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        return reader.ReadLearningOverviewAsync(now, ct);
    }
}
