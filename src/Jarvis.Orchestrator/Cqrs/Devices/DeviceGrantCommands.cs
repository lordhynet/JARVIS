using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Cqrs.Devices;

public sealed record GrantDeviceCapability(string DeviceId, GrantDeviceCapabilityRequest Request) : ICommand<DeviceCapabilityGrantView>;
public sealed record RevokeDeviceCapabilityGrant(Guid GrantId) : ICommand<bool>;
public sealed record ListDeviceCapabilityGrants(string? DeviceId = null) : IQuery<IReadOnlyList<DeviceCapabilityGrantView>>;

public sealed class DeviceGrantCommandHandlers(DeviceGrantService grants) :
    ICommandHandler<GrantDeviceCapability, DeviceCapabilityGrantView>,
    ICommandHandler<RevokeDeviceCapabilityGrant, bool>
{
    public async Task<DeviceCapabilityGrantView> HandleAsync(GrantDeviceCapability command, CancellationToken ct = default) =>
        View(await grants.GrantAsync(command.DeviceId, command.Request.CapabilityId, command.Request.CapabilityVersion,
            command.Request.ConstraintsJson, command.Request.ExpiresAt, ct));
    public Task<bool> HandleAsync(RevokeDeviceCapabilityGrant command, CancellationToken ct = default) => grants.RevokeAsync(command.GrantId, ct);
    internal static DeviceCapabilityGrantView View(DeviceGrant grant) => new(grant.Id, grant.DeviceId, grant.CapabilityId,
        grant.CapabilityVersion, grant.ConstraintsJson, grant.GrantedAt, grant.ExpiresAt, grant.RevokedAt);
}

public sealed class DeviceGrantQueryHandlers(DeviceGrantService grants) :
    IQueryHandler<ListDeviceCapabilityGrants, IReadOnlyList<DeviceCapabilityGrantView>>
{
    public async Task<IReadOnlyList<DeviceCapabilityGrantView>> HandleAsync(ListDeviceCapabilityGrants query, CancellationToken ct = default) =>
        (await grants.ListAsync(query.DeviceId, ct)).Select(DeviceGrantCommandHandlers.View).ToArray();
}
