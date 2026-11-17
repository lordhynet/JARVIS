using Jarvis.Api.Privacy;
using Jarvis.Api.Security;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Cqrs;
using Jarvis.Orchestrator.Cqrs.Privacy;

namespace Jarvis.Api.Endpoints;

public static class PrivacyEndpoints
{
    public static void MapPrivacyEndpoints(this WebApplication app)
    {
        var group = app.MapGroup(CoreRoutes.Privacy).WithTags("Privacy").RequireAuthorization(CorePolicies.OwnerOrCompanion);
        group.MapGet(CoreRoutes.PrivacyStatusSuffix, (IQueryHandler<GetPrivacyStatus, PrivacyStatus> h, CancellationToken ct) => h.HandleAsync(new(), ct));
        group.MapGet("/export", async (HttpContext context, TimeProvider clock, IQueryHandler<ExportCore, CoreDataExport> h, CancellationToken ct) =>
        {
            context.Response.Headers.ContentDisposition = "attachment; filename=jarvis-core-export.json";
            return await h.HandleAsync(new(clock.GetUtcNow()), ct);
        }).RequireAuthorization(Jarvis.Api.Security.CorePolicies.PrivacyManage);
        group.MapGet("/retention", (RetentionSettings settings) => settings.Read()).RequireAuthorization(Jarvis.Api.Security.CorePolicies.Owner);
        group.MapGet("/retention/preview", (RetentionSettings settings, TimeProvider clock, IQueryHandler<PreviewRetention, RetentionPreview> h, CancellationToken ct) =>
            h.HandleAsync(new(settings.Read(), clock.GetUtcNow()), ct)).RequireAuthorization(Jarvis.Api.Security.CorePolicies.Owner);
        group.MapGet("/jobs", (IQueryHandler<ListPrivacyJobs, IReadOnlyList<PrivacyJob>> h, CancellationToken ct) => h.HandleAsync(new(), ct)).RequireAuthorization(Jarvis.Api.Security.CorePolicies.Owner);
        group.MapPost("/retention/apply", (ApplyRetentionRequest request, RetentionSettings settings, TimeProvider clock, ICommandHandler<ApplyRetention, PrivacyJob> h, CancellationToken ct) =>
            h.HandleAsync(new(request, settings.Read(), clock.GetUtcNow()), ct)).RequireAuthorization(Jarvis.Api.Security.CorePolicies.PrivacyManage);
        group.MapPost("/erase", (EraseCoreRequest request, HttpContext context, TimeProvider clock, ICommandHandler<EraseCore, PrivacyStatus> h, CancellationToken ct) =>
            h.HandleAsync(new(request, (string)context.Items[ApiKeyMiddleware.FingerprintItem]!, clock.GetUtcNow()), ct)).RequireAuthorization(Jarvis.Api.Security.CorePolicies.PrivacyManage);
        group.MapPost("/reopen", (ReopenCoreRequest request, HttpContext context, ICommandHandler<ReopenCore, PrivacyStatus> h, CancellationToken ct) =>
            h.HandleAsync(new(request, (string)context.Items[ApiKeyMiddleware.FingerprintItem]!), ct)).RequireAuthorization(Jarvis.Api.Security.CorePolicies.PrivacyManage);
    }
}
