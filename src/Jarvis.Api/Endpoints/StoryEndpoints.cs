using Jarvis.Contracts;
using Jarvis.Orchestrator.Cqrs;
using Jarvis.Orchestrator.Cqrs.Stories;

namespace Jarvis.Api.Endpoints;

public static class StoryEndpoints
{
    public static void MapStoryEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/stories").WithTags("Story Bank");
        group.MapGet("/", (IQueryHandler<ListStories, IReadOnlyList<Story>> handler, CancellationToken ct) =>
            handler.HandleAsync(new ListStories(), ct));
        group.MapGet("/{id:guid}", (Guid id, IQueryHandler<GetStory, Story> handler, CancellationToken ct) =>
            handler.HandleAsync(new GetStory(id), ct));
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
