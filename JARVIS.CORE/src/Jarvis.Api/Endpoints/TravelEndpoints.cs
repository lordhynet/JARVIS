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
        group.MapPost("/simulate", (TravelSimulationRequest request, IConfiguration config, IHostEnvironment environment, ICommandHandler<SimulateTravel, TravelSimulationResult> h, CancellationToken ct) => {
            Jarvis.Orchestrator.Runtime.Guard.Require(!environment.IsProduction() && config.GetValue("Travel:AllowSimulation", false), 403, "Travel simulation is disabled and is never available in Production.");
            return h.HandleAsync(new(request), ct);
        });
    }
}
