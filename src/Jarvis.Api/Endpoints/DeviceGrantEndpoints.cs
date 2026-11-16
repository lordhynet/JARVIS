using Jarvis.Contracts;
using Jarvis.Orchestrator.Cqrs;
using Jarvis.Orchestrator.Cqrs.Devices;

namespace Jarvis.Api.Endpoints;

public static class DeviceGrantEndpoints
{
    public static void MapDeviceGrantEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(CoreRoutes.DeviceGrants, (string id, GrantDeviceCapabilityRequest request,
            ICommandHandler<GrantDeviceCapability, DeviceCapabilityGrantView> handler, CancellationToken ct) =>
            handler.HandleAsync(new GrantDeviceCapability(id, request), ct)).WithTags("Device grants").RequireAuthorization(Jarvis.Api.Security.CorePolicies.DeviceGrants);
        app.MapGet("/api/device-grants", (string? deviceId,
            IQueryHandler<ListDeviceCapabilityGrants, IReadOnlyList<DeviceCapabilityGrantView>> handler, CancellationToken ct) =>
            handler.HandleAsync(new ListDeviceCapabilityGrants(deviceId), ct)).WithTags("Device grants").RequireAuthorization(Jarvis.Api.Security.CorePolicies.DeviceGrants);
        app.MapDelete("/api/device-grants/{id:guid}", (Guid id, ICommandHandler<RevokeDeviceCapabilityGrant, bool> handler, CancellationToken ct) =>
            handler.HandleAsync(new RevokeDeviceCapabilityGrant(id), ct)).WithTags("Device grants").RequireAuthorization(Jarvis.Api.Security.CorePolicies.DeviceGrants);
    }
}
