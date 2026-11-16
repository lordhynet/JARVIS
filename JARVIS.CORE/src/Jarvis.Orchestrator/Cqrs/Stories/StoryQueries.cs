using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Cqrs.Stories;

public sealed record ListStories : IQuery<IReadOnlyList<Story>>;
public sealed record GetStory(Guid Id) : IQuery<Story>;

public sealed class StoryQueryHandlers(IRuntimeReader reader) :
    IQueryHandler<ListStories, IReadOnlyList<Story>>, IQueryHandler<GetStory, Story>
{
    public Task<IReadOnlyList<Story>> HandleAsync(ListStories query, CancellationToken ct = default) =>
        reader.ReadAsync<IReadOnlyList<Story>>(s => s.Stories.Values.OrderByDescending(x => x.UpdatedAt)
            .ThenBy(x => x.Id).ToList(), ct);

    public Task<Story> HandleAsync(GetStory query, CancellationToken ct = default) =>
        reader.ReadAsync(s => s.Stories.TryGetValue(query.Id, out var story) ? story :
            throw new DomainException(404, "Story not found."), ct);
}
