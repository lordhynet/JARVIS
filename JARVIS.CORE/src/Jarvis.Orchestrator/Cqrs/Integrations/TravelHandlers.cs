using Jarvis.Contracts;
using Jarvis.Orchestrator.Travel;

namespace Jarvis.Orchestrator.Cqrs.Integrations;

public sealed record ListTravelRoutes : IQuery<IReadOnlyList<TravelRoute>>;
public sealed record GetTravelCatalogue : IQuery<IReadOnlyList<TravelLine>>;
public sealed record SaveTravelRoute(Guid? Id, SaveTravelRouteRequest Request) : ICommand<TravelRoute>;
public sealed record DeleteTravelRoute(Guid Id, int Revision) : ICommand<bool>;
public sealed class TravelQueries(TravelService travel) : IQueryHandler<ListTravelRoutes, IReadOnlyList<TravelRoute>>,
    IQueryHandler<GetTravelCatalogue, IReadOnlyList<TravelLine>>
{
    public Task<IReadOnlyList<TravelRoute>> HandleAsync(ListTravelRoutes q, CancellationToken ct = default) => travel.ListAsync(ct);
    public Task<IReadOnlyList<TravelLine>> HandleAsync(GetTravelCatalogue q, CancellationToken ct = default) => travel.CatalogueAsync(ct);
}
public sealed class TravelCommands(TravelService travel) : ICommandHandler<SaveTravelRoute, TravelRoute>, ICommandHandler<DeleteTravelRoute, bool>
{
    public Task<TravelRoute> HandleAsync(SaveTravelRoute c, CancellationToken ct = default) => travel.SaveAsync(c.Id, c.Request, ct);
    public Task<bool> HandleAsync(DeleteTravelRoute c, CancellationToken ct = default) => travel.DeleteAsync(c.Id, c.Revision, ct);
}
