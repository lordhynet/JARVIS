using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Cqrs.Devices;

public sealed record ProposeDeviceCommand(string DeviceId, ProposeDeviceCommandRequest Request) : ICommand<DeviceCommandView>;
public sealed record ConfirmDeviceCommand(Guid Id, ConfirmDeviceCommandRequest Request, BudgetActor Actor) : ICommand<DeviceCommandView>;
public sealed record GetDeviceCommand(Guid Id) : IQuery<DeviceCommandView>;

public sealed class DeviceCommandLifecycleHandlers(DeviceCommandService commands) :
    ICommandHandler<ProposeDeviceCommand, DeviceCommandView>,
    ICommandHandler<ConfirmDeviceCommand, DeviceCommandView>
{
    public Task<DeviceCommandView> HandleAsync(ProposeDeviceCommand command, CancellationToken ct = default) =>
        commands.ProposeAsync(command.DeviceId, command.Request, ct);

    public Task<DeviceCommandView> HandleAsync(ConfirmDeviceCommand command, CancellationToken ct = default) =>
        commands.ConfirmAsync(command.Id, command.Request, command.Actor, ct);
}

public sealed class DeviceCommandQueryHandler(IDeviceCommandReader commands, TimeProvider clock) : IQueryHandler<GetDeviceCommand, DeviceCommandView>
{
    public async Task<DeviceCommandView> HandleAsync(GetDeviceCommand query, CancellationToken ct = default)
    {
        var command = await commands.GetAsync(query.Id, ct);
        Guard.Require(command is not null, 404, "Device command was not found.");
        return command.View(clock.GetUtcNow());
    }
}
