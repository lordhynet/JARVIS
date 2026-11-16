using Jarvis.Api.Security;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Cqrs;
using Jarvis.Orchestrator.Cqrs.Devices;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Api.Endpoints;

public static class DeviceCommandEndpoints
{
    public static void MapDeviceCommandEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost(CoreRoutes.DeviceCommandProposals, (string id, ProposeDeviceCommandRequest request,
            ICommandHandler<ProposeDeviceCommand, DeviceCommandView> handler, CancellationToken ct) =>
            handler.HandleAsync(new ProposeDeviceCommand(id, request), ct))
            .WithTags("Device commands").RequireAuthorization(CorePolicies.Owner);

        app.MapGet(CoreRoutes.DeviceCommand, (Guid id, IQueryHandler<GetDeviceCommand, DeviceCommandView> handler, CancellationToken ct) =>
            handler.HandleAsync(new GetDeviceCommand(id), ct))
            .WithTags("Device commands").RequireAuthorization(CorePolicies.Owner);

        app.MapPost(CoreRoutes.DeviceCommandConfirm, (Guid id, ConfirmDeviceCommandRequest request,
            HttpContext context, OwnerAuthorization owner, ICommandHandler<ConfirmDeviceCommand, DeviceCommandView> handler, CancellationToken ct) =>
            handler.HandleAsync(new ConfirmDeviceCommand(id, request, owner.Require(context, DeviceCommandService.ConfirmScope)), ct))
            .WithTags("Device commands").RequireAuthorization(CorePolicies.DeviceCommandConfirm);
    }
}
