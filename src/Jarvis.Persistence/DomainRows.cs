using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Domains;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Persistence;

public sealed class DomainDefinitionRow
{
    public Guid Id { get; set; }
    public string Key { get; set; } = "";
    public string WorkflowKey { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public DomainStatus Status { get; set; }
    public Guid? CurrentVersionId { get; set; }
    public Guid LatestVersionId { get; set; }
    public int Revision { get; set; }
    public bool BuiltIn { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DomainDefinition Contract() => new(Id, Key, WorkflowKey, DisplayName, Status, CurrentVersionId, LatestVersionId, Revision, BuiltIn, CreatedAt, UpdatedAt);
    public static DomainDefinitionRow From(DomainDefinition x) => new() { Id=x.Id, Key=x.Key, WorkflowKey=x.WorkflowKey, DisplayName=x.DisplayName, Status=x.Status,
        CurrentVersionId=x.CurrentVersionId, LatestVersionId=x.LatestVersionId, Revision=x.Revision, BuiltIn=x.BuiltIn, CreatedAt=x.CreatedAt, UpdatedAt=x.UpdatedAt };
}
public sealed class DomainVersionRow
{
    public Guid Id { get; set; }
    public Guid DomainId { get; set; }
    public int Version { get; set; }
    public int SchemaVersion { get; set; }
    public string ManifestJson { get; set; } = "{}";
    public string ManifestHash { get; set; } = "";
    public string CreatedBy { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DomainVersion Contract() => new(Id, DomainId, Version, SchemaVersion, JsonSerializer.Deserialize<DomainManifest>(ManifestJson, DomainPolicy.Json)!, ManifestHash, CreatedBy, CreatedAt);
    public static DomainVersionRow From(DomainVersion x) => new() { Id=x.Id, DomainId=x.DomainId, Version=x.Version, SchemaVersion=x.SchemaVersion,
        ManifestJson=DomainPolicy.Serialize(x.Manifest), ManifestHash=x.ManifestHash, CreatedBy=x.CreatedBy, CreatedAt=x.CreatedAt };
}
public sealed class DomainCapabilityBindingRow
{
    public Guid DomainVersionId { get; set; }
    public string CapabilityId { get; set; } = "";
    public string VersionConstraint { get; set; } = "";
    public bool IsRequired { get; set; }
    public DomainCapabilityBinding Contract() => new(DomainVersionId, CapabilityId, VersionConstraint, IsRequired);
    public static DomainCapabilityBindingRow From(DomainCapabilityBinding x) => new() { DomainVersionId=x.DomainVersionId, CapabilityId=x.CapabilityId, VersionConstraint=x.VersionConstraint, IsRequired=x.IsRequired };
}
public sealed class DomainApprovalRow
{
    public Guid Id { get; set; }
    public Guid DomainVersionId { get; set; }
    public string SubjectId { get; set; } = "";
    public string Decision { get; set; } = "";
    public string PolicyVersion { get; set; } = "";
    public DateTimeOffset At { get; set; }
    public Guid RequestId { get; set; }
    public string Fingerprint { get; set; } = "";
    public int AppliedRevision { get; set; }
    public string Reason { get; set; } = "";
    public DomainApproval Contract() => new(Id, DomainVersionId, SubjectId, Decision, PolicyVersion, At, RequestId, Fingerprint, AppliedRevision, Reason);
    public static DomainApprovalRow From(DomainApproval x) => new() { Id=x.Id, DomainVersionId=x.DomainVersionId, SubjectId=x.SubjectId, Decision=x.Decision,
        PolicyVersion=x.PolicyVersion, At=x.At, RequestId=x.RequestId, Fingerprint=x.Fingerprint, AppliedRevision=x.AppliedRevision, Reason=x.Reason };
}
public sealed class UserDomainStateRow
{
    public string SubjectId { get; set; } = "";
    public Guid DomainId { get; set; }
    public bool Enabled { get; set; }
    public string PreferencesJson { get; set; } = "{}";
    public Guid? ProgressRef { get; set; }
    public int Revision { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public UserDomainState Contract() => new(SubjectId, DomainId, Enabled, JsonSerializer.Deserialize<DomainPreferences>(PreferencesJson)!, ProgressRef, Revision, UpdatedAt);
    public static UserDomainStateRow From(UserDomainState x) => new() { SubjectId=x.SubjectId, DomainId=x.DomainId, Enabled=x.Enabled,
        PreferencesJson=JsonSerializer.Serialize(x.Preferences), ProgressRef=x.ProgressRef, Revision=x.Revision, UpdatedAt=x.UpdatedAt };
}
public sealed class DomainAuditEventRow
{
    public Guid Id { get; set; }
    public Guid DomainId { get; set; }
    public Guid? VersionId { get; set; }
    public string Action { get; set; } = "";
    public string Actor { get; set; } = "";
    public Guid RequestId { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public string DetailsJson { get; set; } = "{}";
    public DomainAuditEvent Contract() => new(Id, DomainId, VersionId, Action, Actor, RequestId, OccurredAt, DetailsJson);
    public static DomainAuditEventRow From(DomainAuditEvent x) => new() { Id=x.Id, DomainId=x.DomainId, VersionId=x.VersionId, Action=x.Action,
        Actor=x.Actor, RequestId=x.RequestId, OccurredAt=x.OccurredAt, DetailsJson=x.DetailsJson };
}
internal static class DomainModel
{
    public static void Configure(ModelBuilder model)
    {
        model.Entity<DomainDefinitionRow>(b => {
            b.ToTable("DomainDefinitions"); b.HasKey(x => x.Id); b.Property(x => x.Key).HasMaxLength(60); b.HasIndex(x => x.Key).IsUnique();
            b.Property(x => x.WorkflowKey).HasMaxLength(90); b.HasIndex(x => x.WorkflowKey).IsUnique(); b.Property(x => x.DisplayName).HasMaxLength(120);
            b.Property(x => x.Revision).IsConcurrencyToken(); b.HasIndex(x => x.Status);
            b.HasOne<DomainVersionRow>().WithMany().HasForeignKey(x => x.CurrentVersionId).OnDelete(DeleteBehavior.SetNull);
        });
        model.Entity<DomainVersionRow>(b => {
            b.ToTable("DomainVersions"); b.HasKey(x => x.Id); b.Property(x => x.ManifestJson).HasColumnType("jsonb");
            b.Property(x => x.ManifestHash).HasMaxLength(64); b.Property(x => x.CreatedBy).HasMaxLength(120);
            b.HasIndex(x => new { x.DomainId, x.Version }).IsUnique(); b.HasOne<DomainDefinitionRow>().WithMany().HasForeignKey(x => x.DomainId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<DomainCapabilityBindingRow>(b => {
            b.ToTable("DomainCapabilityBindings"); b.HasKey(x => new { x.DomainVersionId, x.CapabilityId });
            b.Property(x => x.CapabilityId).HasMaxLength(100); b.Property(x => x.VersionConstraint).HasMaxLength(30);
            b.HasOne<DomainVersionRow>().WithMany().HasForeignKey(x => x.DomainVersionId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<DomainApprovalRow>(b => {
            b.ToTable("DomainApprovals"); b.HasKey(x => x.Id); b.HasIndex(x => x.RequestId).IsUnique(); b.Property(x => x.SubjectId).HasMaxLength(120);
            b.Property(x => x.Decision).HasMaxLength(30); b.Property(x => x.PolicyVersion).HasMaxLength(60); b.Property(x => x.Fingerprint).HasMaxLength(64); b.Property(x => x.Reason).HasMaxLength(500);
            b.HasOne<DomainVersionRow>().WithMany().HasForeignKey(x => x.DomainVersionId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<UserDomainStateRow>(b => {
            b.ToTable("UserDomainState"); b.HasKey(x => new { x.SubjectId, x.DomainId }); b.Property(x => x.SubjectId).HasMaxLength(80);
            b.Property(x => x.PreferencesJson).HasColumnType("jsonb"); b.Property(x => x.Revision).IsConcurrencyToken(); b.HasIndex(x => x.Enabled);
            b.HasOne<DomainDefinitionRow>().WithMany().HasForeignKey(x => x.DomainId).OnDelete(DeleteBehavior.Cascade);
        });
        model.Entity<DomainAuditEventRow>(b => {
            b.ToTable("DomainAuditEvents"); b.HasKey(x => x.Id); b.HasIndex(x => x.RequestId).IsUnique(); b.HasIndex(x => new { x.DomainId, x.OccurredAt });
            b.Property(x => x.Action).HasMaxLength(40); b.Property(x => x.Actor).HasMaxLength(120); b.Property(x => x.DetailsJson).HasColumnType("jsonb");
            b.HasOne<DomainDefinitionRow>().WithMany().HasForeignKey(x => x.DomainId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
