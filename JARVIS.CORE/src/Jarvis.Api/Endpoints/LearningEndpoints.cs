using Jarvis.Contracts;
using Jarvis.Orchestrator.Cqrs;
using Jarvis.Orchestrator.Cqrs.Learning;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Api.Endpoints;

public static class LearningEndpoints
{
    public static void MapLearningEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/learning").WithTags("Learning").RequireAuthorization(Jarvis.Api.Security.CorePolicies.Owner);
        group.MapGet("/", (bool? due, IQueryHandler<ListLearningItems, IReadOnlyList<LearningItem>> handler, CancellationToken ct) =>
            handler.HandleAsync(new ListLearningItems(due ?? false), ct));
        group.MapPost("/", (CreateLearningRequest request, HttpContext context, ICommandHandler<CreateLearningItem, LearningItem> handler, CancellationToken ct) => {
            Guard.Require(request.RequestId is null || context.Request.Headers[CoreHeaders.DataEpoch].Count == 1,
                400, "Idempotent creation requires an explicit data-epoch header.");
            return handler.HandleAsync(new CreateLearningItem(request), ct);
        });
        group.MapPost("/{id:guid}/reviews", (Guid id, ReviewLearningRequest request, ICommandHandler<ReviewLearningItem, LearningItem> handler, CancellationToken ct) =>
            handler.HandleAsync(new ReviewLearningItem(id, request), ct));
        group.MapGet("/{id:guid}/reviews", (Guid id, IQueryHandler<GetReviewHistory, IReadOnlyList<ReviewEvent>> handler, CancellationToken ct) =>
            handler.HandleAsync(new GetReviewHistory(id), ct));
    }
}
