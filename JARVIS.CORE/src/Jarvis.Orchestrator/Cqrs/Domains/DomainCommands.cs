using Jarvis.Contracts;
using Jarvis.Orchestrator.Domains;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Cqrs.Domains;

public sealed record ProposeDomain(ProposeDomainRequest Request) : ICommand<DomainProposal>;
public sealed record SaveDomainDraft(SaveDomainDraftRequest Request, string Actor) : ICommand<DomainPreview>;
public sealed record ActivateDomain(string Key, ActivateDomainRequest Request, BudgetActor Actor) : ICommand<DomainOperationReceipt>;
public sealed record DisableDomain(string Key, DisableDomainRequest Request, BudgetActor Actor) : ICommand<DomainOperationReceipt>;
public sealed record SaveUserDomainState(string Key, SaveDomainStateRequest Request) : ICommand<UserDomainState>;
public sealed record ListDomains : IQuery<IReadOnlyList<DomainPreview>>;
public sealed record PreviewDomain(string Key, Guid? VersionId) : IQuery<DomainPreview>;
public sealed record GetUserDomainState(string Key) : IQuery<UserDomainState>;

public sealed class DomainCommandHandlers(IDomainFactory factory, IDomainRegistry registry) :
    ICommandHandler<ProposeDomain, DomainProposal>, ICommandHandler<SaveDomainDraft, DomainPreview>,
    ICommandHandler<ActivateDomain, DomainOperationReceipt>, ICommandHandler<DisableDomain, DomainOperationReceipt>,
    ICommandHandler<SaveUserDomainState, UserDomainState>
{
    public Task<DomainProposal> HandleAsync(ProposeDomain command, CancellationToken ct = default) => factory.ProposeAsync(command.Request, ct);
    public Task<DomainPreview> HandleAsync(SaveDomainDraft command, CancellationToken ct = default) => registry.SaveDraftAsync(command.Request, command.Actor, ct);
    public Task<DomainOperationReceipt> HandleAsync(ActivateDomain command, CancellationToken ct = default) => registry.ActivateAsync(command.Key, command.Request, command.Actor, ct);
    public Task<DomainOperationReceipt> HandleAsync(DisableDomain command, CancellationToken ct = default) => registry.DisableAsync(command.Key, command.Request, command.Actor, ct);
    public Task<UserDomainState> HandleAsync(SaveUserDomainState command, CancellationToken ct = default) => registry.SaveStateAsync(command.Key, command.Request, ct);
}

public sealed class DomainQueryHandlers(IDomainRegistry registry) :
    IQueryHandler<ListDomains, IReadOnlyList<DomainPreview>>, IQueryHandler<PreviewDomain, DomainPreview>,
    IQueryHandler<GetUserDomainState, UserDomainState>
{
    public Task<IReadOnlyList<DomainPreview>> HandleAsync(ListDomains query, CancellationToken ct = default) => registry.ListAsync(ct);
    public Task<DomainPreview> HandleAsync(PreviewDomain query, CancellationToken ct = default) => registry.PreviewAsync(query.Key, query.VersionId, ct);
    public Task<UserDomainState> HandleAsync(GetUserDomainState query, CancellationToken ct = default) => registry.GetStateAsync(query.Key, ct);
}
