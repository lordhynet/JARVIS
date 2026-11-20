using Jarvis.Orchestrator.Forge;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Persistence;

// Two existing Forge table sketches, one shared ciphertext shape; neither contains business classifications.
public sealed class ForgeProtectedRow
{
    public string OwnerScope { get; set; } = "";
    public Guid ProgrammeId { get; set; }
    public Guid Id { get; set; }
    public long Epoch { get; set; }
    public int Revision { get; set; }
    public Guid RequestId { get; set; }
    public int ExpectedRevision { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public int Version { get; set; }
    public string KeyId { get; set; } = "";
    public byte[] Nonce { get; set; } = [];
    public byte[] Ciphertext { get; set; } = [];
    public byte[] Tag { get; set; } = [];
    public int RetryVersion { get; set; }
    public string RetryKeyId { get; set; } = "";
    public byte[] RetryMac { get; set; } = [];

    public ForgeProtectedRecord Record(ForgeProtectedKind kind) => new(kind, OwnerScope, ProgrammeId, Id, Epoch, Revision, RequestId,
        ExpectedRevision, CreatedAt, new(Version, KeyId, [.. Nonce], [.. Ciphertext], [.. Tag]), new(RetryVersion, RetryKeyId, [.. RetryMac]));
    public static ForgeProtectedRow From(ForgeProtectedRecord record) => new() {
        OwnerScope = record.OwnerScope, ProgrammeId = record.ProgrammeId, Id = record.Id, Epoch = record.Epoch, Revision = record.Revision,
        RequestId = record.RequestId, ExpectedRevision = record.ExpectedRevision, CreatedAt = record.CreatedAt,
        Version = record.Envelope.Version, KeyId = record.Envelope.KeyId, Nonce = [.. record.Envelope.Nonce], Ciphertext = [.. record.Envelope.Ciphertext], Tag = [.. record.Envelope.Tag],
        RetryVersion = record.RetryProof.Version, RetryKeyId = record.RetryProof.KeyId, RetryMac = [.. record.RetryProof.Mac]
    };
    public static void Configure(ModelBuilder model) {
        foreach (var name in new[] { "ForgeCheckIns", "ForgeCapacity" }) model.SharedTypeEntity<ForgeProtectedRow>(name, b => {
            b.ToTable(name, table => table.HasCheckConstraint("CK_" + name + "_Envelope", """
                "Revision">0 AND "ExpectedRevision">=0 AND "ExpectedRevision"::bigint+1="Revision" AND "Epoch">=0
                AND "Version"=1 AND "RetryVersion"=1 AND octet_length("Nonce")=12 AND octet_length("Tag")=16
                AND octet_length("Ciphertext")<=65536 AND octet_length("RetryMac")=32
                """));
            b.HasKey(x => new { x.OwnerScope, x.ProgrammeId, x.Id, x.Revision });
            b.Property(x => x.OwnerScope).HasMaxLength(80); b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.KeyId).HasMaxLength(64); b.Property(x => x.RetryKeyId).HasMaxLength(64);
            b.HasIndex(x => new { x.OwnerScope, x.ProgrammeId, x.RequestId }).IsUnique();
            b.HasOne<ForgeProgrammeRow>().WithMany().HasForeignKey(x => new { x.OwnerScope, x.ProgrammeId }).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
