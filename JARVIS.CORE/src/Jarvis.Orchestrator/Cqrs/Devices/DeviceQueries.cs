using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Cqrs.Devices;

public sealed record ListDevices : IQuery<IReadOnlyList<DeviceView>>;

public sealed class DeviceQueryHandler(IRuntimeReader reader, TimeProvider clock) : IQueryHandler<ListDevices, IReadOnlyList<DeviceView>>
{
    public Task<IReadOnlyList<DeviceView>> HandleAsync(ListDevices query, CancellationToken ct = default) =>
        reader.ReadAsync<IReadOnlyList<DeviceView>>(s => s.Devices.Values.OrderBy(d => d.Registration.Name)
            .Select(d => new DeviceView(d, clock.GetUtcNow() - d.LastSeenAt <= DeviceService.OnlineWindow)).ToList(), ct);
}
