using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Cqrs.Devices;

public sealed record GrantDeviceCapability(string DeviceId, GrantDeviceCapabilityRequest Request, BudgetActor Actor) : ICommand<DeviceCapabilityGrantView>;
public sealed record RevokeDeviceCapabilityGrant(Guid GrantId) : ICommand<bool>;
public sealed record ListDeviceCapabilityGrants(string? DeviceId = null) : IQuery<IReadOnlyList<DeviceCapabilityGrantView>>;

public sealed class DeviceGrantCommandHandlers(DeviceGrantService grants) :
    ICommandHandler<GrantDeviceCapability, DeviceCapabilityGrantView>,
    ICommandHandler<RevokeDeviceCapabilityGrant, bool>
{
    public Task<DeviceCapabilityGrantView> HandleAsync(GrantDeviceCapability command, CancellationToken ct = default) =>
        grants.GrantAsync(command.DeviceId, command.Request, command.Actor, ct);
    public Task<bool> HandleAsync(RevokeDeviceCapabilityGrant command, CancellationToken ct = default) => grants.RevokeAsync(command.GrantId, ct);
}

public sealed class DeviceGrantQueryHandlers(DeviceGrantService grants) :
    IQueryHandler<ListDeviceCapabilityGrants, IReadOnlyList<DeviceCapabilityGrantView>>
{
    public Task<IReadOnlyList<DeviceCapabilityGrantView>> HandleAsync(ListDeviceCapabilityGrants query, CancellationToken ct = default) =>
        grants.ListAsync(query.DeviceId, ct);
}
