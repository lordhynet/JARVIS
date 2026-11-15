using Jarvis.Api.Security;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Domains;
using Jarvis.Orchestrator.Cqrs;
using Jarvis.Orchestrator.Cqrs.Domains;

namespace Jarvis.Api.Endpoints;

public static class DomainEndpoints
{
    public static void MapDomainEndpoints(this WebApplication app)
    {
        app.MapPost("/api/domains/proposals", (ProposeDomainRequest request, ICommandHandler<ProposeDomain, DomainProposal> handler, CancellationToken ct) => handler.HandleAsync(new(request), ct));
        app.MapGet("/api/domains", (IQueryHandler<ListDomains, IReadOnlyList<DomainPreview>> handler, CancellationToken ct) => handler.HandleAsync(new(), ct));
        app.MapGet("/api/domains/{key}/preview", (string key, Guid? versionId, IQueryHandler<PreviewDomain, DomainPreview> handler, CancellationToken ct) => handler.HandleAsync(new(key, versionId), ct));
        app.MapPost("/api/domains/drafts", (SaveDomainDraftRequest request, ICommandHandler<SaveDomainDraft, DomainPreview> handler, CancellationToken ct) => handler.HandleAsync(new(request with { Provenance = null }, "manual-client"), ct));
        foreach (var action in new[] { "activate", "rollback" })
            app.MapPost("/api/domains/{key}/" + action, (string key, ActivateDomainRequest request, HttpContext context, OwnerAuthorization owner, ICommandHandler<ActivateDomain, DomainOperationReceipt> handler, CancellationToken ct) =>
                handler.HandleAsync(new(key, request, owner.Require(context, "domain.manage")), ct));
        app.MapPost("/api/domains/{key}/disable", (string key, DisableDomainRequest request, HttpContext context, OwnerAuthorization owner, ICommandHandler<DisableDomain, DomainOperationReceipt> handler, CancellationToken ct) =>
            handler.HandleAsync(new(key, request, owner.Require(context, "domain.manage")), ct));
        app.MapGet("/api/domains/{key}/state", (string key, IQueryHandler<GetUserDomainState, UserDomainState> handler, CancellationToken ct) => handler.HandleAsync(new(key), ct));
        app.MapPut("/api/domains/{key}/state", (string key, SaveDomainStateRequest request, ICommandHandler<SaveUserDomainState, UserDomainState> handler, CancellationToken ct) => handler.HandleAsync(new(key, request), ct));
    }
}
