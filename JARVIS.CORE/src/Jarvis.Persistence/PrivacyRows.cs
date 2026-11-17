using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Persistence;

public sealed class PrivacyControlRow
{
    public int Id { get; set; } = 1;
    public long Epoch { get; set; }
    public int Revision { get; set; } = 1;
    public bool Erased { get; set; }
    public DateTimeOffset? ErasedAt { get; set; }
    public Guid? ErasureRequestId { get; set; }
    public string? ErasureKeyFingerprint { get; set; }
    public PrivacyStatus Status() => new(Epoch, Revision, Erased, ErasedAt, ErasureRequestId);
}
public sealed class PrivacyJobRow
{
    public Guid Id { get; set; }
    public string Kind { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public DateTimeOffset CompletedAt { get; set; }
    public string CountsJson { get; set; } = "{}";
    public PrivacyJob Contract() => new(Id, Kind, Fingerprint, CompletedAt, System.Text.Json.JsonSerializer.Deserialize<PrivacyCounts>(CountsJson)!);
}
internal static class PostgresPrivacyGate
{
    public static async Task<PrivacyStatus> LockAsync(SessionDbContext db, CancellationToken ct, bool accountingOnly = false)
    {
        var rows = await db.PrivacyControls.FromSqlRaw("SELECT * FROM \"PrivacyControls\" WHERE \"Id\"=1 FOR SHARE").AsNoTracking().ToListAsync(ct);
        var status = rows.Single().Status();
        if (!accountingOnly) PrivacyContext.RequireWrite(status);
        return status;
    }
}
