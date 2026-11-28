using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Cqrs.Stories;

public sealed record CreateStory(CreateStoryRequest Request) : ICommand<Story>;
public sealed record UpdateStory(Guid Id, UpdateStoryRequest Request) : ICommand<Story>;
public sealed record DeleteStory(Guid Id, int ExpectedVersion) : ICommand<CommandCompleted>;

public sealed class StoryCommandHandlers(IRuntimeStore store, TimeProvider clock) :
    ICommandHandler<CreateStory, Story>, ICommandHandler<UpdateStory, Story>,
    ICommandHandler<DeleteStory, CommandCompleted>
{
    public Task<Story> HandleAsync(CreateStory command, CancellationToken ct = default)
    {
        Guard.Require(command.Request.RequestId != Guid.Empty, 400, "RequestId is required.");
        var content = Validate(command.Request.Content);
        return store.UpdateAsync(s =>
        {
            // The client-generated request ID is also the resource ID; retries cannot duplicate a story.
            if (s.Stories.TryGetValue(command.Request.RequestId, out var existing))
            {
                Guard.Require(existing.Version == 1 && Same(existing.Content, content), 409,
                    "RequestId already belongs to a different or edited story.");
                return existing;
            }
            var now = clock.GetUtcNow();
            var story = new Story(command.Request.RequestId, content, 1, now, now);
            s.Stories.Add(story.Id, story);
            return story;
        }, ct);
    }

    public Task<Story> HandleAsync(UpdateStory command, CancellationToken ct = default)
    {
        var content = Validate(command.Request.Content);
        // Generated practice shown this story is purged in the same transaction (lineage trigger).
        return Jarvis.Orchestrator.Training.GeneratedPracticeTriggers.UpdateAsync(store, Jarvis.Orchestrator.Training.PracticeLineage.Story, command.Id, s =>
        {
            var story = Current(s, command.Id, command.Request.ExpectedVersion);
            var updated = story with { Content = content, Version = checked(story.Version + 1), UpdatedAt = clock.GetUtcNow() };
            s.Stories[story.Id] = updated;
            return updated;
        }, clock, ct);
    }

    public Task<CommandCompleted> HandleAsync(DeleteStory command, CancellationToken ct = default) =>
        Jarvis.Orchestrator.Training.GeneratedPracticeTriggers.UpdateAsync(store, Jarvis.Orchestrator.Training.PracticeLineage.Story, command.Id, s =>
        {
            Current(s, command.Id, command.ExpectedVersion);
            foreach (var attempt in s.CoachingAttempts.Values.Where(x => x.StoryId == command.Id && x.DeletedAt is null).ToList())
                Jarvis.Orchestrator.Coaching.CoachingService.Purge(s, attempt, clock.GetUtcNow());
            s.Stories.Remove(command.Id);
            foreach (var claim in s.SourceClaims.Values.Where(x => x.StoryId == command.Id).ToList()) s.SourceClaims.Remove(claim.Id);
            Jarvis.Orchestrator.Workflows.AgentRuntime.InvalidateSource(s, "story", command.Id);
            return new CommandCompleted();
        }, clock, ct);

    private static Story Current(RuntimeState state, Guid id, int version)
    {
        Guard.Require(version > 0, 400, "ExpectedVersion must be positive.");
        Guard.Require(state.Stories.TryGetValue(id, out var story), 404, "Story not found.");
        Guard.Require(story.Version == version, 409, "Story changed. Reload before editing or deleting.");
        return story;
    }

    private static bool Same(StoryContent a, StoryContent b) =>
        System.Text.Json.JsonSerializer.Serialize(a) == System.Text.Json.JsonSerializer.Serialize(b);

    private static StoryContent Validate(StoryContent? content)
    {
        Guard.Require(content is not null, 400, "Story content is required.");
        string Optional(string? text) => string.IsNullOrWhiteSpace(text) ? "" : Guard.Text(text, "Story field", 8000);
        string[] Items(string[]? items)
        {
            Guard.Require(items is null || items.Length <= 30, 400, "At most 30 questions or claims are allowed.");
            return (items ?? []).Select(x => Guard.Text(x, "Question or claim", 2000)).ToArray();
        }
        return content with
        {
            Title = Guard.Text(content.Title, "Title", 200),
            CompanyProject = Guard.Text(content.CompanyProject, "Company/project", 300),
            ServiceFeature = Guard.Text(content.ServiceFeature, "Service/feature", 300),
            Context = Optional(content.Context), Problem = Optional(content.Problem), Constraints = Optional(content.Constraints),
            PersonalOwnership = Optional(content.PersonalOwnership), TechnicalDecisions = Optional(content.TechnicalDecisions),
            TradeOffs = Optional(content.TradeOffs), Implementation = Optional(content.Implementation),
            Collaboration = Optional(content.Collaboration), FailureModes = Optional(content.FailureModes),
            Result = Optional(content.Result), Metrics = Optional(content.Metrics), ChangeToday = Optional(content.ChangeToday),
            FollowUpQuestions = Items(content.FollowUpQuestions), CvClaims = Items(content.CvClaims)
        };
    }
}
