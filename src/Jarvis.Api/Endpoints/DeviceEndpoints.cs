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
        var group = app.MapGroup("/api/devices").WithTags("Devices");
        group.MapGet("/", async (HttpContext context, IQueryHandler<ListDevices, IReadOnlyList<DeviceView>> handler, CancellationToken ct) => {
            var devices = await handler.HandleAsync(new ListDevices(), ct);
            return context.Items[ApiKeyMiddleware.ActorItem] is CoreActor { DeviceId: { } id }
                ? devices.Where(d => d.Device.Registration.DeviceId == id).ToArray() : devices;
        });
        group.MapPost("/{id}/credential", (string id, ICommandHandler<IssueDeviceCredential, IssuedDeviceCredential> handler, CancellationToken ct) => handler.HandleAsync(new IssueDeviceCredential(id), ct));
        group.MapDelete("/{id}/credential", (string id, ICommandHandler<RevokeDeviceCredential, bool> handler, CancellationToken ct) => handler.HandleAsync(new RevokeDeviceCredential(id), ct));
        group.MapPost("/", (RegisterDeviceRequest request, ICommandHandler<RegisterDevice, RegisteredDevice> handler, CancellationToken ct) =>
            handler.HandleAsync(new RegisterDevice(request), ct));
        group.MapPost("/{id}/heartbeat", (string id, ICommandHandler<HeartbeatDevice, RegisteredDevice> handler, CancellationToken ct) =>
            handler.HandleAsync(new HeartbeatDevice(id), ct));
        group.MapPost("/{id}/verify", (string id, ICommandHandler<VerifyDevice, RegisteredDevice> handler, CancellationToken ct) =>
            handler.HandleAsync(new VerifyDevice(id), ct));
    }
}
