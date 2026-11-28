using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Training;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Persistence;

// Session-private generated practice questions (design: docs/practice/GENERATED_PRACTICE_DESIGN.md). Slice 2a creates the
// empty table and a read path only; no application code writes rows. Provenance/Lineage columns are reserved for the
// first writer, which must arrive together with its privacy and lifecycle contracts.
internal sealed class GeneratedPracticeQuestionRow
{
    public Guid Id { get; set; }
    public Guid SessionId { get; set; }
    public Guid OperationId { get; set; }
    public int Topic { get; set; }
    public string OutlineKey { get; set; } = "";
    public int OutlineVersion { get; set; }
    public int Version { get; set; }
    public string Fingerprint { get; set; } = "";
    public string? Json { get; set; }
    public string Provenance { get; set; } = "{}";
    public string Lineage { get; set; } = "[]";
    public bool Verified { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PurgedAt { get; set; }

    public static void Configure(ModelBuilder model) => model.Entity<GeneratedPracticeQuestionRow>(b => {
        b.ToTable("GeneratedPracticeQuestions"); b.HasKey(x => x.Id); b.Property(x => x.Id).ValueGeneratedNever();
        b.Property(x => x.OutlineKey).HasMaxLength(64); b.Property(x => x.Fingerprint).HasMaxLength(64);
        b.Property(x => x.Json).HasColumnType("jsonb"); b.Property(x => x.Provenance).HasColumnType("jsonb"); b.Property(x => x.Lineage).HasColumnType("jsonb");
        b.HasIndex(x => x.OperationId).IsUnique();
        b.HasIndex(x => new { x.SessionId, x.CreatedAt });
        b.HasIndex(x => new { x.SessionId, x.Topic, x.OutlineKey });
    });

    private sealed record Payload(string Prompt, string ReferenceOutline, IReadOnlyList<string> FollowUps);

    // Fixed refusals: never echo stored text in an error.
    public GeneratedPracticeQuestion Contract()
    {
        if (Id == Guid.Empty || SessionId == Guid.Empty || !Enum.IsDefined((LearningDomain)Topic) || Version < 1 || OutlineKey.Length is 0 or > 64)
            throw new InvalidDataException("Invalid generated practice question row.");
        if (PurgedAt is not null || Json is null)
            return new(Id, SessionId, (LearningDomain)Topic, OutlineKey, OutlineVersion, Version, null, null, [], CreatedAt, PurgedAt ?? CreatedAt);
        Payload? payload;
        try { payload = JsonSerializer.Deserialize<Payload>(Json); }
        catch (JsonException) { throw new InvalidDataException("Invalid generated practice question row."); }
        if (payload is not { Prompt.Length: > 0, ReferenceOutline: not null, FollowUps: not null } || payload.FollowUps.Any(f => f is null))
            throw new InvalidDataException("Invalid generated practice question row.");
        return new(Id, SessionId, (LearningDomain)Topic, OutlineKey, OutlineVersion, Version, payload.Prompt, payload.ReferenceOutline,
            payload.FollowUps.ToArray(), CreatedAt, null);
    }
}

// Session-bound reads under the privacy gate. A row from another session is never returned.
public sealed class PostgresGeneratedPracticeQuestionReader(IDbContextFactory<SessionDbContext> factory) : IGeneratedPracticeQuestionReader
{
    public async Task<GeneratedPracticeQuestion?> GetAsync(Guid sessionId, Guid id, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await PostgresPrivacyGate.LockAsync(db, ct);
        var row = await db.GeneratedPracticeQuestions.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id && r.SessionId == sessionId, ct);
        var result = row?.Contract();
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<IReadOnlyList<GeneratedPracticeQuestion>> ListAsync(Guid sessionId, CancellationToken ct = default)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        await PostgresPrivacyGate.LockAsync(db, ct);
        var rows = await db.GeneratedPracticeQuestions.AsNoTracking().Where(r => r.SessionId == sessionId)
            .OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).Take(200).ToArrayAsync(ct);
        var result = rows.Select(r => r.Contract()).ToArray();
        await tx.CommitAsync(ct);
        return result;
    }
}
