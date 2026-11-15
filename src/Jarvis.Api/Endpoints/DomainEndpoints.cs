using Jarvis.Api.Security;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Domains;

namespace Jarvis.Api.Endpoints;

public static class DomainEndpoints
{
    public static void MapDomainEndpoints(this WebApplication app)
    {
        app.MapGet("/api/domains", (IDomainRegistry registry, CancellationToken ct) => registry.ListAsync(ct));
        app.MapGet("/api/domains/{key}/preview", (string key, Guid? versionId, IDomainRegistry registry, CancellationToken ct) => registry.PreviewAsync(key, versionId, ct));
        app.MapPost("/api/domains/drafts", (SaveDomainDraftRequest request, IDomainRegistry registry, CancellationToken ct) => registry.SaveDraftAsync(request, "manual-client", ct));
        foreach (var action in new[] { "activate", "rollback" })
            app.MapPost("/api/domains/{key}/" + action, (string key, ActivateDomainRequest request, HttpContext context, OwnerAuthorization owner, IDomainRegistry registry, CancellationToken ct) =>
                registry.ActivateAsync(key, request, owner.Require(context, "domain.manage"), ct));
        app.MapPost("/api/domains/{key}/disable", (string key, DisableDomainRequest request, HttpContext context, OwnerAuthorization owner, IDomainRegistry registry, CancellationToken ct) =>
            registry.DisableAsync(key, request, owner.Require(context, "domain.manage"), ct));
        app.MapGet("/api/domains/{key}/state", (string key, IDomainRegistry registry, CancellationToken ct) => registry.GetStateAsync(key, ct));
        app.MapPut("/api/domains/{key}/state", (string key, SaveDomainStateRequest request, IDomainRegistry registry, CancellationToken ct) => registry.SaveStateAsync(key, request, ct));
    }
}
