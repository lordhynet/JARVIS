using Jarvis.Contracts;
using Jarvis.Orchestrator.Cqrs;
using Jarvis.Orchestrator.Cqrs.Stories;

namespace Jarvis.Api.Endpoints;

public static class StoryEndpoints
{
    public static void MapStoryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/stories").WithTags("Story Bank").RequireAuthorization(Jarvis.Api.Security.CorePolicies.Owner);
        group.MapGet("/", (IQueryHandler<ListStories, IReadOnlyList<Story>> handler, CancellationToken ct) =>
            handler.HandleAsync(new ListStories(), ct));
        group.MapGet("/{id:guid}", (Guid id, IQueryHandler<GetStory, Story> handler, CancellationToken ct) =>
            handler.HandleAsync(new GetStory(id), ct));
        // Deterministic interview-readiness checklist (no AI, read-only); the list is least-ready first.
        group.MapGet("/readiness", (IQueryHandler<ListStoryReadiness, IReadOnlyList<StoryReadiness>> handler, CancellationToken ct) =>
            handler.HandleAsync(new ListStoryReadiness(), ct));
        group.MapGet("/{id:guid}/readiness", (Guid id, IQueryHandler<GetStoryReadiness, StoryReadiness> handler, CancellationToken ct) =>
            handler.HandleAsync(new GetStoryReadiness(id), ct));
        group.MapPost("/", (CreateStoryRequest request, ICommandHandler<CreateStory, Story> handler, CancellationToken ct) =>
            handler.HandleAsync(new CreateStory(request), ct));
        group.MapPut("/{id:guid}", (Guid id, UpdateStoryRequest request, ICommandHandler<UpdateStory, Story> handler, CancellationToken ct) =>
            handler.HandleAsync(new UpdateStory(id, request), ct));
        group.MapDelete("/{id:guid}", async (Guid id, int expectedVersion, ICommandHandler<DeleteStory, CommandCompleted> handler, CancellationToken ct) =>
        {
            await handler.HandleAsync(new DeleteStory(id, expectedVersion), ct);
            return Results.NoContent();
        });
    }
}
