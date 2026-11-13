using Jarvis.Contracts;
using Jarvis.Orchestrator.Cqrs;
using Jarvis.Orchestrator.Cqrs.Learning;

namespace Jarvis.Api.Endpoints;

public static class LearningEndpoints
{
    public static void MapLearningEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/learning").WithTags("Learning");
        group.MapGet("/", (bool? due, IQueryHandler<ListLearningItems, IReadOnlyList<LearningItem>> handler, CancellationToken ct) =>
            handler.HandleAsync(new ListLearningItems(due ?? false), ct));
        group.MapPost("/", (CreateLearningRequest request, ICommandHandler<CreateLearningItem, LearningItem> handler, CancellationToken ct) =>
            handler.HandleAsync(new CreateLearningItem(request), ct));
        group.MapPost("/{id:guid}/reviews", (Guid id, ReviewLearningRequest request, ICommandHandler<ReviewLearningItem, LearningItem> handler, CancellationToken ct) =>
            handler.HandleAsync(new ReviewLearningItem(id, request), ct));
        group.MapGet("/{id:guid}/reviews", (Guid id, IQueryHandler<GetReviewHistory, IReadOnlyList<ReviewEvent>> handler, CancellationToken ct) =>
            handler.HandleAsync(new GetReviewHistory(id), ct));
    }
}
