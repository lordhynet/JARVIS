using Jarvis.Contracts;
using Jarvis.Orchestrator.Forge;
using Jarvis.Orchestrator.Runtime;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Persistence;

public sealed class ForgeProgrammeRow
{
    public string OwnerScope { get; set; } = "";
    public Guid Id { get; set; }
    public int Revision { get; set; }
    public int ConfigurationVersion { get; set; }
    public DateOnly Start { get; set; }
    public DateOnly Graduation { get; set; }
    public string TimeZone { get; set; } = "";
    public bool Paused { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public ForgeProgramme Contract() => new(Id, OwnerScope, Revision, ConfigurationVersion, Start, Graduation, TimeZone, Paused, CreatedAt, UpdatedAt, DeletedAt);
    public void Apply(ForgeProgramme programme) {
        Revision = programme.Revision; ConfigurationVersion = programme.ConfigurationVersion; Start = programme.Start; Graduation = programme.Graduation;
        TimeZone = programme.TimeZone; Paused = programme.Paused; CreatedAt = programme.CreatedAt; UpdatedAt = programme.UpdatedAt; DeletedAt = programme.DeletedAt;
    }
    public static void Configure(ModelBuilder model) => model.Entity<ForgeProgrammeRow>(b => {
        b.ToTable("ForgeProgrammes"); b.HasKey(x => new { x.OwnerScope, x.Id });
        b.Property(x => x.OwnerScope).HasMaxLength(80); b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.TimeZone).HasMaxLength(100); b.Property(x => x.Revision).IsConcurrencyToken();
        b.HasIndex(x => new { x.OwnerScope, x.CreatedAt });
    });
}

public sealed class PostgresForgeProgrammeReader(IDbContextFactory<SessionDbContext> factory) : IForgeProgrammeReader
{
    public async Task<ForgeProgramme?> GetAsync(string ownerScope, Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await PostgresPrivacyGate.LockAsync(db, ct);
        var row = await db.ForgeProgrammes.AsNoTracking().SingleOrDefaultAsync(x => x.OwnerScope == ownerScope && x.Id == id, ct);
        await tx.CommitAsync(ct); return row?.Contract();
    }
    public async Task<IReadOnlyList<ForgeProgramme>> ListAsync(string ownerScope, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await PostgresPrivacyGate.LockAsync(db, ct);
        var rows = await db.ForgeProgrammes.AsNoTracking().Where(x => x.OwnerScope == ownerScope && x.DeletedAt == null)
            .OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).ToListAsync(ct);
        await tx.CommitAsync(ct); return rows.Select(x => x.Contract()).ToArray();
    }
}
