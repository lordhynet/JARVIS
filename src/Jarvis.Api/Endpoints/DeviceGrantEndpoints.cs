using Jarvis.Api.Security;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Cqrs;
using Jarvis.Orchestrator.Cqrs.Devices;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Api.Endpoints;

public static class DeviceGrantEndpoints
{
    public static void MapDeviceGrantEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(CoreRoutes.DeviceGrants, (string id, GrantDeviceCapabilityRequest request, HttpContext context, OwnerAuthorization owner,
            ICommandHandler<GrantDeviceCapability, DeviceCapabilityGrantView> handler, CancellationToken ct) =>
            handler.HandleAsync(new GrantDeviceCapability(id, request, owner.Require(context, DeviceGrantService.ManageScope)), ct))
            .WithTags("Device grants").RequireAuthorization(CorePolicies.DeviceGrants);
        app.MapGet("/api/device-grants", (string? deviceId,
            IQueryHandler<ListDeviceCapabilityGrants, IReadOnlyList<DeviceCapabilityGrantView>> handler, CancellationToken ct) =>
            handler.HandleAsync(new ListDeviceCapabilityGrants(deviceId), ct)).WithTags("Device grants").RequireAuthorization(Jarvis.Api.Security.CorePolicies.DeviceGrants);
        app.MapDelete("/api/device-grants/{id:guid}", (Guid id, ICommandHandler<RevokeDeviceCapabilityGrant, bool> handler, CancellationToken ct) =>
            handler.HandleAsync(new RevokeDeviceCapabilityGrant(id), ct)).WithTags("Device grants").RequireAuthorization(Jarvis.Api.Security.CorePolicies.DeviceGrants);
    }
}
