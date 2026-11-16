using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Persistence;

public sealed class PostgresDeviceCommandStore(IDbContextFactory<SessionDbContext> factory) : IDeviceCommandReader
{
    public async Task<DeviceCommand?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await PostgresPrivacyGate.LockAsync(db, ct);
        var row = await db.DeviceCommands.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        await tx.CommitAsync(ct);
        return row?.Contract();
    }

    internal static async Task<DeviceCommand?> ReadLockedAsync(SessionDbContext db, Guid id, CancellationToken ct)
    {
        var rows = await db.DeviceCommands.FromSql($"""SELECT * FROM "DeviceCommands" WHERE "Id" = {id} FOR UPDATE""").ToListAsync(ct);
        return rows.SingleOrDefault()?.Contract();
    }

    // The shared Core transaction holds the runtime lock and reads the command before writing it.
    internal static async Task SaveAsync(SessionDbContext db, DeviceCommand command, CancellationToken ct)
    {
        var row = db.DeviceCommands.Local.SingleOrDefault(x => x.Id == command.Id);
        if (row is null)
            db.DeviceCommands.Add(DeviceCommandRow.From(command));
        else
            row.ApplyDecision(command);
        await db.SaveChangesAsync(ct);
    }

    internal static IQueryable<DeviceCommandRow> Prunable(SessionDbContext db, RetentionPolicy policy, DateTimeOffset asOf)
    {
        var cutoff = asOf.AddDays(-policy.ArtifactDays);
        return db.DeviceCommands.Where(x => x.ExpiresAt <= asOf && x.CreatedAt < cutoff);
    }
}

public sealed class DeviceCommandRow
{
    public Guid Id { get; set; }
    public Guid GrantId { get; set; }
    public string DeviceId { get; set; } = "";
    public string CapabilityId { get; set; } = "";
    public string CapabilityVersion { get; set; } = "";
    public string CredentialHash { get; set; } = "";
    public string ArgumentsJson { get; set; } = "";
    public string ArgumentHash { get; set; } = "";
    public InvocationOrigin Origin { get; set; }
    public DeviceCommandStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConfirmedAt { get; set; }
    public string? ConfirmedBy { get; set; }
    public DateTimeOffset? CancelledAt { get; set; }
    public string? CancelledBy { get; set; }

    public DeviceCommand Contract() => new(Id, GrantId, DeviceId, CapabilityId, CapabilityVersion, CredentialHash,
        ArgumentsJson, ArgumentHash, Origin, Status, CreatedAt, ExpiresAt, ConfirmedAt, ConfirmedBy, CancelledAt, CancelledBy);

    internal static DeviceCommandRow From(DeviceCommand command)
    {
        var row = new DeviceCommandRow
        {
            Id = command.Id, GrantId = command.GrantId, DeviceId = command.DeviceId, CapabilityId = command.CapabilityId,
            CapabilityVersion = command.CapabilityVersion, CredentialHash = command.CredentialHash, ArgumentsJson = command.ArgumentsJson,
            ArgumentHash = command.ArgumentHash, Origin = command.Origin, CreatedAt = command.CreatedAt, ExpiresAt = command.ExpiresAt
        };
        row.ApplyDecision(command);
        return row;
    }

    internal void ApplyDecision(DeviceCommand command)
    {
        Status = command.Status;
        ConfirmedAt = command.ConfirmedAt;
        ConfirmedBy = command.ConfirmedBy;
        CancelledAt = command.CancelledAt;
        CancelledBy = command.CancelledBy;
    }
}
