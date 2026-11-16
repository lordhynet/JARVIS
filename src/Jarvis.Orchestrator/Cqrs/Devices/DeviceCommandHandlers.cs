using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Cqrs.Devices;

public sealed record ProposeDeviceCommand(string DeviceId, ProposeDeviceCommandRequest Request) : ICommand<DeviceCommandView>;
public sealed record ConfirmDeviceCommand(Guid Id, ConfirmDeviceCommandRequest Request, BudgetActor Actor) : ICommand<DeviceCommandView>;
public sealed record ClaimDeviceCommand(ClaimDeviceCommandRequest Request) : ICommand<DeliveredDeviceCommand?>;
public sealed record GetDeviceCommand(Guid Id) : IQuery<DeviceCommandView>;
public sealed record ListDeviceCommandsForReview : IQuery<IReadOnlyList<DeviceCommandView>>;

public sealed class DeviceCommandLifecycleHandlers(DeviceCommandService commands) :
    ICommandHandler<ProposeDeviceCommand, DeviceCommandView>,
    ICommandHandler<ConfirmDeviceCommand, DeviceCommandView>,
    ICommandHandler<ClaimDeviceCommand, DeliveredDeviceCommand?>
{
    public Task<DeviceCommandView> HandleAsync(ProposeDeviceCommand command, CancellationToken ct = default) =>
        commands.ProposeAsync(command.DeviceId, command.Request, ct);

    public Task<DeviceCommandView> HandleAsync(ConfirmDeviceCommand command, CancellationToken ct = default) =>
        commands.ConfirmAsync(command.Id, command.Request, command.Actor, ct);

    public Task<DeliveredDeviceCommand?> HandleAsync(ClaimDeviceCommand command, CancellationToken ct = default) =>
        commands.ClaimAsync(command.Request, ct);
}

public sealed class DeviceCommandQueryHandler(IDeviceCommandReader commands, TimeProvider clock) :
    IQueryHandler<GetDeviceCommand, DeviceCommandView>, IQueryHandler<ListDeviceCommandsForReview, IReadOnlyList<DeviceCommandView>>
{
    public async Task<IReadOnlyList<DeviceCommandView>> HandleAsync(ListDeviceCommandsForReview query, CancellationToken ct = default)
    {
        var rows = await commands.ListForReviewAsync(clock.GetUtcNow(), ct);
        var now = clock.GetUtcNow();
        return rows.Select(x => x.View(now)).ToArray();
    }

    public async Task<DeviceCommandView> HandleAsync(GetDeviceCommand query, CancellationToken ct = default)
    {
        var command = await commands.GetAsync(query.Id, ct);
        Guard.Require(command is not null, 404, "Device command was not found.");
        return command.View(clock.GetUtcNow());
    }
}
