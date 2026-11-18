using Jarvis.Api.Security;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Api.Endpoints;

public static class BrowserAccessEndpoints
{
    public static void MapBrowserAccessEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(BrowserOwnerAccess.StatusRoute, (HttpContext context, IConfiguration config) => {
            var actor = (CoreActor)context.Items[ApiKeyMiddleware.ActorItem]!;
            return new { enrolled = actor.IsOwnerBrowser, deviceId = actor.DeviceId,
                legacyKeyAllowed = !BrowserOwnerAccess.RequiresEnrollment(config), elevationSeconds = 300,
                scopes = BrowserOwnerAccess.AllowedScopes.Order(StringComparer.Ordinal).ToArray() };
        }).RequireAuthorization(CorePolicies.Owner);
        app.MapPost(BrowserOwnerAccess.EnrollmentRoute, (RegisterDeviceRequest request, DeviceCredentials credentials, CancellationToken ct) =>
            credentials.EnrolBrowserAsync(request, ct)).RequireAuthorization(CorePolicies.DeviceManage);
        app.MapPost(BrowserOwnerAccess.ElevationRoute, (OwnerElevationRequest request, HttpContext context, OwnerAuthorization owner, BrowserOwnerAccess access, CancellationToken ct) => {
            _ = owner.RequireCredential(context, "device.manage");
            return access.IssueAsync((CoreActor)context.Items[ApiKeyMiddleware.ActorItem]!, request, ct);
        }).RequireAuthorization(CorePolicies.Owner);
        app.MapDelete(BrowserOwnerAccess.ElevationRoute, (HttpContext context, BrowserOwnerAccess access, CancellationToken ct) => {
            var actor = (CoreActor)context.Items[ApiKeyMiddleware.ActorItem]!;
            Guard.Require(actor.IsOwnerBrowser, 403, "Use an enrolled browser to revoke its owner access.");
            return access.RevokeAsync(actor, ct);
        }).RequireAuthorization(CorePolicies.Owner);
    }
}
