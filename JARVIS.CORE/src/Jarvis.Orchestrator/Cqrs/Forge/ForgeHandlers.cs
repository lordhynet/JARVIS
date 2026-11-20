using Jarvis.Contracts;
using Jarvis.Orchestrator.Forge;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Cqrs.Forge;

public sealed record CreateForgeProgramme(CreateForgeProgrammeRequest Request) : ICommand<ForgeProgramme>;
public sealed record PauseForgeProgramme(Guid Id, PauseForgeProgrammeRequest Request) : ICommand<ForgeProgramme>;
public sealed record DeleteForgeProgramme(Guid Id, int Revision) : ICommand<bool>;
public sealed record GetForgeProgramme(Guid Id) : IQuery<ForgeProgrammeView>;
public sealed record ListForgeProgrammes : IQuery<IReadOnlyList<ForgeProgrammeView>>;

public sealed class ForgeCommands(ForgeProgrammeService service) :
    ICommandHandler<CreateForgeProgramme, ForgeProgramme>, ICommandHandler<PauseForgeProgramme, ForgeProgramme>, ICommandHandler<DeleteForgeProgramme, bool>
{
    public Task<ForgeProgramme> HandleAsync(CreateForgeProgramme c, CancellationToken ct = default) => service.CreateAsync(c.Request, ct);
    public Task<ForgeProgramme> HandleAsync(PauseForgeProgramme c, CancellationToken ct = default) => service.PauseAsync(c.Id, c.Request, ct);
    public Task<bool> HandleAsync(DeleteForgeProgramme c, CancellationToken ct = default) => service.DeleteAsync(c.Id, c.Revision, ct);
}

public sealed class ForgeQueries(IForgeProgrammeReader reader, IForgeAvailability feature, TimeProvider clock) :
    IQueryHandler<GetForgeProgramme, ForgeProgrammeView>, IQueryHandler<ListForgeProgrammes, IReadOnlyList<ForgeProgrammeView>>
{
    public async Task<ForgeProgrammeView> HandleAsync(GetForgeProgramme q, CancellationToken ct = default)
    {
        ForgeProgrammePolicy.RequireEnabled(feature);
        var programme = await reader.GetAsync(ForgeProgrammePolicy.OwnerScope, q.Id, ct);
        Guard.Require(programme is { DeletedAt: null }, 404, "Forge programme not found.");
        return ForgeProgrammePolicy.View(programme, clock.GetUtcNow());
    }
    public async Task<IReadOnlyList<ForgeProgrammeView>> HandleAsync(ListForgeProgrammes q, CancellationToken ct = default)
    {
        ForgeProgrammePolicy.RequireEnabled(feature);
        var now = clock.GetUtcNow();
        return (await reader.ListAsync(ForgeProgrammePolicy.OwnerScope, ct)).Select(x => ForgeProgrammePolicy.View(x, now)).ToArray();
    }
}
