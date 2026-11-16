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
        app.MapPost(CoreRoutes.DeviceCommandClaim, async (ClaimDeviceCommandRequest request, HttpContext context,
            ICommandHandler<ClaimDeviceCommand, DeliveredDeviceCommand?> handler, CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var delivery = await handler.HandleAsync(new ClaimDeviceCommand(request), ct);
            return delivery is null ? Results.NoContent() : Results.Ok(delivery);
        }).WithTags("Device commands").RequireAuthorization(CorePolicies.Companion);

        app.MapPost(CoreRoutes.DeviceCommandReceipts, (Guid id, SubmitDeviceReceiptRequest request, HttpContext context,
            ICommandHandler<RecordDeviceReceipt, DeviceReceiptView> handler, CancellationToken ct) =>
            handler.HandleAsync(new RecordDeviceReceipt(VerifiedDeviceReceipt.Verify(id, request,
                ApiKeyMiddleware.SuppliedCredential(context.Request))), ct))
            .WithTags("Device commands").RequireAuthorization(CorePolicies.Companion);

        app.MapGet(CoreRoutes.DeviceCommandReceipts, (Guid id,
            IQueryHandler<ListDeviceReceipts, IReadOnlyList<DeviceReceiptView>> handler, CancellationToken ct) =>
            handler.HandleAsync(new ListDeviceReceipts(id), ct))
            .WithTags("Device commands").RequireAuthorization(CorePolicies.Owner);

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
