using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Persistence;

public sealed class PostgresDeviceReceiptStore(IDbContextFactory<SessionDbContext> factory) : IDeviceReceiptReader
{
    public async Task<IReadOnlyList<DeviceReceiptView>> ListAsync(Guid commandId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await PostgresPrivacyGate.LockAsync(db, ct);
        var receipts = await ListAsync(db, commandId, ct);
        await tx.CommitAsync(ct);
        return receipts;
    }

    internal static async Task<IReadOnlyList<DeviceReceiptView>> ListAsync(SessionDbContext db, Guid commandId, CancellationToken ct)
    {
        var rows = await db.DeviceReceipts.AsNoTracking().Where(x => x.CommandId == commandId)
            .OrderBy(x => x.ReceivedAt).ThenBy(x => x.IsFinal).ToArrayAsync(ct);
        return rows.Select(x => x.View()).ToArray();
    }
}

public sealed class DeviceReceiptRow
{
    public Guid CommandId { get; set; }
    public Guid Id { get; set; }
    public Guid DispatchRequestId { get; set; }
    public DeviceReceiptKind Kind { get; set; }
    public string PayloadJson { get; set; } = "";
    public DateTimeOffset ReceivedAt { get; set; }
    public bool Late { get; set; }
    public bool IsFinal { get; set; }

    public DeviceReceiptView View() => new(Id, CommandId, DispatchRequestId, Kind, PayloadJson, ReceivedAt, Late);

    internal static DeviceReceiptRow From(DeviceReceiptView receipt) => new()
    {
        CommandId = receipt.CommandId, Id = receipt.Id, DispatchRequestId = receipt.DispatchRequestId, Kind = receipt.Kind,
        PayloadJson = receipt.PayloadJson, ReceivedAt = receipt.ReceivedAt, Late = receipt.Late,
        IsFinal = receipt.Kind != DeviceReceiptKind.Acknowledged
    };
}
