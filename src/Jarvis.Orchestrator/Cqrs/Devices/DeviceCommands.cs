using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Cqrs.Devices;

public sealed record RegisterDevice(RegisterDeviceRequest Request) : ICommand<RegisteredDevice>;
public sealed record HeartbeatDevice(string DeviceId) : ICommand<RegisteredDevice>;
public sealed record VerifyDevice(string DeviceId) : ICommand<RegisteredDevice>;
public sealed record IssueDeviceCredential(string DeviceId) : ICommand<IssuedDeviceCredential>;
public sealed record RevokeDeviceCredential(string DeviceId) : ICommand<bool>;

public sealed class DeviceCommandHandlers(DeviceService devices, DeviceCredentials credentials) :
    ICommandHandler<RegisterDevice, RegisteredDevice>,
    ICommandHandler<HeartbeatDevice, RegisteredDevice>,
    ICommandHandler<VerifyDevice, RegisteredDevice>,
    ICommandHandler<IssueDeviceCredential, IssuedDeviceCredential>,
    ICommandHandler<RevokeDeviceCredential, bool>
{
    public Task<RegisteredDevice> HandleAsync(RegisterDevice command, CancellationToken ct = default) => devices.RegisterAsync(command.Request, ct);
    public Task<RegisteredDevice> HandleAsync(HeartbeatDevice command, CancellationToken ct = default) => devices.HeartbeatAsync(command.DeviceId, ct);
    public Task<RegisteredDevice> HandleAsync(VerifyDevice command, CancellationToken ct = default) => devices.VerifyAsync(command.DeviceId, ct);
    public Task<IssuedDeviceCredential> HandleAsync(IssueDeviceCredential command, CancellationToken ct = default) => credentials.IssueAsync(command.DeviceId, ct);
    public Task<bool> HandleAsync(RevokeDeviceCredential command, CancellationToken ct = default) => credentials.RevokeAsync(command.DeviceId, ct);
}
