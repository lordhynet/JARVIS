using Jarvis.Api.Security;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Cqrs;
using Jarvis.Orchestrator.Cqrs.Forge;
using Jarvis.Orchestrator.Forge;

namespace Jarvis.Api.Endpoints;

public static class ForgeEndpoints
{
    public const string Root = "/api/forge";
    public static void MapForgeEndpoints(this IEndpointRouteBuilder app)
    {
        // JARVIS deliberately forbids undeclared endpoints. Declare only a 404 boundary
        // while disabled, so its fallback policy does not turn feature-off into 403.
        if (!app.ServiceProvider.GetRequiredService<IForgeAvailability>().Enabled) {
            app.Map(Root + "/{**path}", (HttpContext context) => {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            }).RequireAuthorization(CorePolicies.Owner);
            return;
        }
        var group = app.MapGroup(Root + "/programmes").WithTags("Forge").RequireAuthorization(CorePolicies.Owner);
        group.MapPost("/", (CreateForgeProgrammeRequest request, ICommandHandler<CreateForgeProgramme, ForgeProgramme> h, CancellationToken ct) => h.HandleAsync(new(request), ct));
        group.MapGet("/", (IQueryHandler<ListForgeProgrammes, IReadOnlyList<ForgeProgrammeView>> h, CancellationToken ct) => h.HandleAsync(new(), ct));
        group.MapGet("/{id:guid}", (Guid id, IQueryHandler<GetForgeProgramme, ForgeProgrammeView> h, CancellationToken ct) => h.HandleAsync(new(id), ct));
        group.MapPatch("/{id:guid}/preferences", (Guid id, PauseForgeProgrammeRequest request, ICommandHandler<PauseForgeProgramme, ForgeProgramme> h, CancellationToken ct) => h.HandleAsync(new(id, request), ct));
        group.MapDelete("/{id:guid}", (Guid id, int revision, ICommandHandler<DeleteForgeProgramme, bool> h, CancellationToken ct) => h.HandleAsync(new(id, revision), ct));
        // PF-02: installs division templates as registry drafts only; activation stays the owner's approval step.
        var divisions = app.MapGroup(Root + "/divisions").WithTags("Forge").RequireAuthorization(CorePolicies.Owner);
        divisions.MapPost("/drafts", (InstallForgeDivisionsRequest request, ICommandHandler<InstallForgeDivisionDrafts, IReadOnlyList<ForgeDivisionDraft>> h, CancellationToken ct) => h.HandleAsync(new(request), ct));
        // PF-09: read-only Today view; the same block the spoken briefing uses, with protected goal titles redacted.
        app.MapGet(Root + "/today", (IQueryHandler<GetForgeToday, ForgeBriefing> h, CancellationToken ct) => h.HandleAsync(new(), ct))
            .WithTags("Forge").RequireAuthorization(CorePolicies.Owner);
        // J4: read-only weekly summary; counts only, unknowns stay null.
        app.MapGet(Root + "/week", (IQueryHandler<GetForgeWeek, ForgeWeek> h, CancellationToken ct) => h.HandleAsync(new(), ct))
            .WithTags("Forge").RequireAuthorization(CorePolicies.Owner);
    }
}
