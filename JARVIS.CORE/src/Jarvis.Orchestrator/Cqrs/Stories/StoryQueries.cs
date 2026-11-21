using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Cqrs.Stories;

public sealed record ListStories : IQuery<IReadOnlyList<Story>>;
public sealed record GetStory(Guid Id) : IQuery<Story>;
public sealed record GetStoryReadiness(Guid Id) : IQuery<StoryReadiness>;
public sealed record ListStoryReadiness : IQuery<IReadOnlyList<StoryReadiness>>;

public sealed class StoryQueryHandlers(IRuntimeReader reader) :
    IQueryHandler<ListStories, IReadOnlyList<Story>>, IQueryHandler<GetStory, Story>,
    IQueryHandler<GetStoryReadiness, StoryReadiness>, IQueryHandler<ListStoryReadiness, IReadOnlyList<StoryReadiness>>
{
    // Read-only, deterministic checklist; least-ready stories first so the owner knows what to work on.
    public Task<StoryReadiness> HandleAsync(GetStoryReadiness query, CancellationToken ct = default) =>
        reader.ReadAsync(s => s.Stories.TryGetValue(query.Id, out var story)
            ? Jarvis.Orchestrator.Coaching.StoryReadinessChecker.Check(story, s.SourceClaims.Values.Count(x => x.StoryId == story.Id))
            : throw new DomainException(404, "Story not found."), ct);

    public Task<IReadOnlyList<StoryReadiness>> HandleAsync(ListStoryReadiness query, CancellationToken ct = default) =>
        reader.ReadAsync<IReadOnlyList<StoryReadiness>>(s => s.Stories.Values
            .Select(story => Jarvis.Orchestrator.Coaching.StoryReadinessChecker.Check(story, s.SourceClaims.Values.Count(x => x.StoryId == story.Id)))
            .OrderBy(x => x.Score).ThenBy(x => x.Title, StringComparer.Ordinal).ThenBy(x => x.StoryId).ToList(), ct);

    public Task<IReadOnlyList<Story>> HandleAsync(ListStories query, CancellationToken ct = default) =>
        reader.ReadAsync<IReadOnlyList<Story>>(s => s.Stories.Values.OrderByDescending(x => x.UpdatedAt)
            .ThenBy(x => x.Id).ToList(), ct);

    public Task<Story> HandleAsync(GetStory query, CancellationToken ct = default) =>
        reader.ReadAsync(s => s.Stories.TryGetValue(query.Id, out var story) ? story :
            throw new DomainException(404, "Story not found."), ct);
}
