using Jarvis.Contracts;
using Jarvis.Orchestrator.Cqrs;
using Jarvis.Orchestrator.Cqrs.Devices;

namespace Jarvis.Api.Endpoints;

public static class DeviceEndpoints
{
    public static void MapDeviceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/devices").WithTags("Devices");
        group.MapGet("/", (IQueryHandler<ListDevices, IReadOnlyList<DeviceView>> handler, CancellationToken ct) =>
            handler.HandleAsync(new ListDevices(), ct));
        group.MapPost("/", (RegisterDeviceRequest request, ICommandHandler<RegisterDevice, RegisteredDevice> handler, CancellationToken ct) =>
            handler.HandleAsync(new RegisterDevice(request), ct));
        group.MapPost("/{id}/heartbeat", (string id, ICommandHandler<HeartbeatDevice, RegisteredDevice> handler, CancellationToken ct) =>
            handler.HandleAsync(new HeartbeatDevice(id), ct));
        group.MapPost("/{id}/verify", (string id, ICommandHandler<VerifyDevice, RegisteredDevice> handler, CancellationToken ct) =>
            handler.HandleAsync(new VerifyDevice(id), ct));
    }
}
