using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Cqrs.Devices;

public sealed record RecordDeviceReceipt(VerifiedDeviceReceipt Receipt) : ICommand<DeviceReceiptView>;
public sealed record ListDeviceReceipts(Guid CommandId) : IQuery<IReadOnlyList<DeviceReceiptView>>;

public sealed class DeviceReceiptCommandHandler(DeviceReceiptService receipts) : ICommandHandler<RecordDeviceReceipt, DeviceReceiptView>
{
    public Task<DeviceReceiptView> HandleAsync(RecordDeviceReceipt command, CancellationToken ct = default) =>
        receipts.RecordAsync(command.Receipt, ct);
}

public sealed class DeviceReceiptQueryHandler(IDeviceReceiptReader receipts) : IQueryHandler<ListDeviceReceipts, IReadOnlyList<DeviceReceiptView>>
{
    public Task<IReadOnlyList<DeviceReceiptView>> HandleAsync(ListDeviceReceipts query, CancellationToken ct = default) =>
        receipts.ListAsync(query.CommandId, ct);
}
