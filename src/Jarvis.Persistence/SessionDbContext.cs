using Jarvis.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using System.Text.Json;

namespace Jarvis.Persistence;

public sealed class SessionDbContext(DbContextOptions<SessionDbContext> options) : DbContext(options)
{
    public DbSet<SessionRow> Sessions => Set<SessionRow>();
    public DbSet<PrivacyControlRow> PrivacyControls => Set<PrivacyControlRow>();
    public DbSet<PrivacyJobRow> PrivacyJobs => Set<PrivacyJobRow>();
    public DbSet<CheckpointRow> Checkpoints => Set<CheckpointRow>();
    public DbSet<RuntimeDocument> RuntimeDocuments => Set<RuntimeDocument>();
    public DbSet<WorkflowRunRow> WorkflowRuns => Set<WorkflowRunRow>();
    public DbSet<WorkflowEventRow> WorkflowEvents => Set<WorkflowEventRow>();
    public DbSet<DomainDefinitionRow> DomainDefinitions => Set<DomainDefinitionRow>();
    public DbSet<DomainVersionRow> DomainVersions => Set<DomainVersionRow>();
    public DbSet<DomainCapabilityBindingRow> DomainCapabilityBindings => Set<DomainCapabilityBindingRow>();
    public DbSet<DomainApprovalRow> DomainApprovals => Set<DomainApprovalRow>();
    public DbSet<UserDomainStateRow> UserDomainState => Set<UserDomainStateRow>();
    public DbSet<DomainAuditEventRow> DomainAuditEvents => Set<DomainAuditEventRow>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        DomainModel.Configure(model);
        model.Entity<PrivacyControlRow>(b => { b.HasKey(x => x.Id); b.HasData(new PrivacyControlRow()); });
        model.Entity<PrivacyJobRow>(b => { b.HasKey(x => x.Id); b.Property(x => x.CountsJson).HasColumnType("jsonb"); b.HasIndex(x => x.CompletedAt); });
        model.Entity<WorkflowRunRow>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Json).HasColumnType("jsonb");
            b.HasIndex(x => x.AvailableAt);
            b.HasIndex(x => x.CreatedAt);
        });
        model.Entity<WorkflowEventRow>(b =>
        {
            b.HasKey(x => x.EventId);
            b.HasIndex(x => new { x.RunId, x.Revision }).IsUnique();
            b.HasOne<WorkflowRunRow>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<RuntimeDocument>(b =>
        {
            b.HasKey(r => r.Id);
            b.Property(r => r.Json).HasColumnType("jsonb");
            b.HasData(new RuntimeDocument { Id = 1, Json = "{}" });
        });
        model.Entity<SessionRow>(b =>
        {
            b.ToTable("Sessions");
            b.HasKey(s => s.Id);
            b.Property(s => s.Title).HasMaxLength(120);
            b.Property(s => s.PendingVoiceProjectionIdsJson).HasColumnType("jsonb");
            b.HasIndex(s => s.UpdatedAt);
            b.HasMany(s => s.Checkpoints).WithOne().HasForeignKey(c => c.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<CheckpointRow>(b =>
        {
            b.ToTable("Checkpoints");
            b.HasKey(c => c.Id);
            b.Property(c => c.Summary).HasMaxLength(4000);
            b.HasIndex(c => new { c.SessionId, c.Sequence }).IsUnique();
        });
    }
}

public sealed class SessionRow
{
    public Guid Id { get; set; }
    public ActivityType Activity { get; set; }
    public string Title { get; set; } = "";
    public SessionStatus Status { get; set; }
    public string? ActiveDeviceId { get; set; }
    public string? CurrentTask { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<CheckpointRow> Checkpoints { get; set; } = [];
    public string? HandoffJson { get; set; }
    public string MessagesJson { get; set; } = "[]";
    public string PendingVoiceProjectionIdsJson { get; set; } = "[]";

    public ConversationSession ToContract() => new(Id, Activity, Title, Status, ActiveDeviceId,
        CurrentTask, CreatedAt, UpdatedAt,
        Checkpoints.OrderBy(c => c.Sequence).Select(c => c.ToContract()).ToList(),
        HandoffJson is null ? null : JsonSerializer.Deserialize<SessionHandoff>(HandoffJson),
        JsonSerializer.Deserialize<List<ChatMessage>>(MessagesJson),
        JsonSerializer.Deserialize<List<Guid>>(PendingVoiceProjectionIdsJson));

    public static SessionRow FromContract(ConversationSession s) => new()
    {
        Id = s.Id, Activity = s.Activity, Title = s.Title, Status = s.Status,
        ActiveDeviceId = s.ActiveDeviceId, CurrentTask = s.CurrentTask,
        CreatedAt = DatabaseTimestamp(s.CreatedAt), UpdatedAt = DatabaseTimestamp(s.UpdatedAt),
        Checkpoints = s.Checkpoints.Select(CheckpointRow.FromContract).ToList(),
        HandoffJson = s.Handoff is null ? null : JsonSerializer.Serialize(s.Handoff),
        MessagesJson = JsonSerializer.Serialize(s.Messages ?? []),
        PendingVoiceProjectionIdsJson = JsonSerializer.Serialize(s.PendingVoiceProjectionIds ?? [])
    };

    // PostgreSQL timestamps store microseconds; normalize responses before returning
    // them so a subsequent read (including after restart) has identical timestamps.
    internal static DateTimeOffset DatabaseTimestamp(DateTimeOffset value) =>
        new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);
}

public sealed class CheckpointRow
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public int Sequence { get; set; }
    public string Summary { get; set; } = "";
    public string? CurrentTask { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    public SessionCheckpoint ToContract() => new(Id, SessionId, Sequence, Summary, CurrentTask, CreatedAt);
    public static CheckpointRow FromContract(SessionCheckpoint c) => new()
    {
        Id = c.Id, SessionId = c.SessionId, Sequence = c.Sequence, Summary = c.Summary,
        CurrentTask = c.CurrentTask, CreatedAt = SessionRow.DatabaseTimestamp(c.CreatedAt)
    };
}

// Used only to generate migrations; no credentials or running database are needed.
public sealed class SessionDbContextFactory : IDesignTimeDbContextFactory<SessionDbContext>
{
    public SessionDbContext CreateDbContext(string[] args) => new(
        new DbContextOptionsBuilder<SessionDbContext>()
            .UseNpgsql("Host=localhost;Database=jarvis").Options);
}
