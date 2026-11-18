using Jarvis.Api.Security;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Cqrs;
using Jarvis.Orchestrator.Cqrs.Integrations;

namespace Jarvis.Api.Endpoints;

public static class TravelEndpoints
{
    public const string Root = "/api/travel";
    public static void MapTravelEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(Root).WithTags("Travel").RequireAuthorization(CorePolicies.Owner);
        group.MapGet("/routes", (IQueryHandler<ListTravelRoutes, IReadOnlyList<TravelRoute>> h, CancellationToken ct) => h.HandleAsync(new(), ct));
        group.MapGet("/lines", (IQueryHandler<GetTravelCatalogue, IReadOnlyList<TravelLine>> h, CancellationToken ct) => h.HandleAsync(new(), ct));
        group.MapPost("/routes", (SaveTravelRouteRequest request, ICommandHandler<SaveTravelRoute, TravelRoute> h, CancellationToken ct) => h.HandleAsync(new(null, request), ct));
        group.MapPut("/routes/{id:guid}", (Guid id, SaveTravelRouteRequest request, ICommandHandler<SaveTravelRoute, TravelRoute> h, CancellationToken ct) => h.HandleAsync(new(id, request), ct));
        group.MapDelete("/routes/{id:guid}", (Guid id, int revision, ICommandHandler<DeleteTravelRoute, bool> h, CancellationToken ct) => h.HandleAsync(new(id, revision), ct));
    }
}
