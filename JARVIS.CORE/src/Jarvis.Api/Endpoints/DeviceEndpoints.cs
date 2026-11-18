using Jarvis.Contracts;
using Jarvis.Orchestrator.Cqrs;
using Jarvis.Orchestrator.Cqrs.Devices;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Api.Security;

namespace Jarvis.Api.Endpoints;

public static class DeviceEndpoints
{
    public static void MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup(CoreRoutes.Devices).WithTags("Devices").RequireAuthorization(CorePolicies.OwnerOrCompanion);
        group.MapGet("/", async (HttpContext context, IConfiguration config, ILoggerFactory logs, IQueryHandler<ListDevices, IReadOnlyList<DeviceView>> handler, CancellationToken ct) => {
            var devices = await handler.HandleAsync(new ListDevices(), ct);
            var displayName = OwnerDisplayName.Read(config, logs.CreateLogger("Jarvis.OwnerDisplayName"));
            devices = devices.Select(d => d with { OwnerDisplayName = displayName }).ToArray();
            return context.Items[ApiKeyMiddleware.ActorItem] is CoreActor { DeviceId: { } id }
                ? devices.Where(d => d.Device.Registration.DeviceId == id).ToArray() : devices;
        });
        group.MapPost(CoreRoutes.DeviceCredentialSuffix, (string id, ICommandHandler<IssueDeviceCredential, IssuedDeviceCredential> handler, CancellationToken ct) => handler.HandleAsync(new IssueDeviceCredential(id), ct)).RequireAuthorization(CorePolicies.DeviceManage);
        group.MapDelete(CoreRoutes.DeviceCredentialSuffix, (string id, ICommandHandler<RevokeDeviceCredential, bool> handler, CancellationToken ct) => handler.HandleAsync(new RevokeDeviceCredential(id), ct)).RequireAuthorization(CorePolicies.DeviceManage);
        group.MapPost("/", (RegisterDeviceRequest request, ICommandHandler<RegisterDevice, RegisteredDevice> handler, CancellationToken ct) =>
            handler.HandleAsync(new RegisterDevice(request), ct)).RequireAuthorization(Jarvis.Api.Security.CorePolicies.Owner);
        group.MapPost(CoreRoutes.DeviceHeartbeatSuffix, (string id, HttpContext context, ICommandHandler<HeartbeatDevice, RegisteredDevice> handler, CancellationToken ct) =>
        {
            ApiKeyMiddleware.RequireDevice(context, id);
            return handler.HandleAsync(new HeartbeatDevice(id), ct);
        });
        group.MapPost("/{id}/verify", (string id, ICommandHandler<VerifyDevice, RegisteredDevice> handler, CancellationToken ct) =>
            handler.HandleAsync(new VerifyDevice(id), ct)).RequireAuthorization(Jarvis.Api.Security.CorePolicies.DeviceVerify);
    }
}
