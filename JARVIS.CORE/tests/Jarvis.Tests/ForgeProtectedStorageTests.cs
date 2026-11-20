using System.Security.Cryptography;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Forge;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Jarvis.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed record SyntheticForgeValue(string Value, int Revision);
public sealed record SyntheticForgeRequest(Guid RequestId, int ExpectedRevision, string Value);

public sealed class ForgeProtectedStorageFixture(IServiceProvider services)
{
    public ICoreTransaction Transaction => services.GetRequiredService<ICoreTransaction>();
    public IPrivacyStore Privacy => services.GetRequiredService<IPrivacyStore>();
    private readonly IProtectedRecordProtector protector = new ProtectedRecordProtector(() => new() {
        ActiveKeyId = "synthetic-only", Keys = new Dictionary<string, string> { ["synthetic-only"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(x => (byte)x).ToArray()) }
    });
    public static readonly DateTimeOffset At = ForgeProgrammePolicy.Timestamp(DateTimeOffset.Parse("2026-11-20T10:40:00Z"));
    public static ForgeProgramme Programme() => new(Guid.NewGuid(), ForgeProgrammePolicy.OwnerScope, 1, 1,
        ForgeCalendar.DefaultStart, ForgeCalendar.DefaultGraduation, ForgeCalendar.DefaultTimeZone, true, At, At);
    public ForgeProtectedRecord Record(ForgeProgramme programme, ForgeProtectedKind kind, Guid id, int revision = 1,
        string value = "Synthetic private observation", long epoch = 0, Guid? request = null)
    {
        var record = new ForgeProtectedRecord(kind, programme.OwnerScope, programme.Id, id, epoch, revision, request ?? Guid.NewGuid(), revision - 1,
            At, new(1, "", [], [], []), new(1, "", []));
        var canonical = JsonSerializer.SerializeToUtf8Bytes(new SyntheticForgeRequest(record.RequestId, record.ExpectedRevision, value));
        try { return record with { Envelope = ForgeProtectedJson.Protect(protector, record.ValueContext, new SyntheticForgeValue(value, revision)), RetryProof = protector.CreateRetryProof(record.RetryContext, canonical) }; }
        finally { CryptographicOperations.ZeroMemory(canonical); }
    }
    public SyntheticForgeValue Open(ForgeProtectedRecord record) => ForgeProtectedJson.Read<SyntheticForgeValue>(protector, record.ValueContext, record.Envelope);
    public bool Matches(ForgeProtectedRecord record, string value, ProtectedRecordContext? context = null) {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new SyntheticForgeRequest(record.RequestId, record.ExpectedRevision, value));
        try { return protector.MatchesRetryProof(context ?? record.RetryContext, record.RetryProof, bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public Task<ForgeProtectedRecord?> Latest(ForgeProtectedRecord record) => Transaction.ExecuteAsync(w =>
        w.ReadForgeProtectedRecordAsync(record.Kind, record.OwnerScope, record.ProgrammeId, record.Id));
    public Task<ForgeProtectedRecord?> Receipt(ForgeProtectedRecord record) => Transaction.ExecuteAsync(w =>
        w.ReadForgeProtectedRequestAsync(record.Kind, record.OwnerScope, record.ProgrammeId, record.RequestId));
    public Task<bool> Append(ForgeProtectedRecord record) => Transaction.ExecuteAsync(async w => { await w.AppendForgeProtectedRecordAsync(record); return true; });

    public async Task KindIsolation()
    {
        var programme = Programme(); var id = Guid.NewGuid(); var requestId = Guid.NewGuid();
        var checkIn = Record(programme, ForgeProtectedKind.CheckIn, id, value: "Synthetic check-in", request: requestId);
        var capacity = Record(programme, ForgeProtectedKind.Capacity, id, value: "Synthetic capacity", request: requestId);
        await Transaction.ExecuteAsync(async w => { await w.SaveForgeProgrammeAsync(programme);
            await w.AppendForgeProtectedRecordAsync(checkIn); await w.AppendForgeProtectedRecordAsync(capacity); return true; });
        Assert.Equal(checkIn.Envelope.Ciphertext, (await Latest(checkIn))!.Envelope.Ciphertext);
        Assert.Equal("Synthetic check-in", Open((await Receipt(checkIn))!).Value);
        Assert.Equal("Synthetic capacity", Open((await Receipt(capacity))!).Value);
        await Append(Record(programme, ForgeProtectedKind.CheckIn, id, 2, "Updated synthetic check-in"));
        Assert.Equal(2, (await Latest(checkIn))!.Revision); Assert.Equal(1, (await Latest(capacity))!.Revision);
        Assert.Equal(capacity.Envelope.Ciphertext, (await Latest(capacity))!.Envelope.Ciphertext);
        Assert.Equal(ForgeProtectedJson.BucketBytes, capacity.Envelope.Ciphertext.Length);
    }

    public async Task<(ForgeProgramme Programme, ForgeProtectedRecord First, ForgeProtectedRecord Latest)> Versions()
    {
        var programme = Programme(); var first = Record(programme, ForgeProtectedKind.CheckIn, Guid.NewGuid());
        var next = Record(programme, ForgeProtectedKind.CheckIn, first.Id, 2, "Corrected synthetic observation");
        var capacity = Record(programme, ForgeProtectedKind.Capacity, Guid.NewGuid(), value: "Synthetic capacity");
        await Transaction.ExecuteAsync(async w => {
            await w.SaveForgeProgrammeAsync(programme); // New, paused parent and protected rows in one transaction.
            await w.AppendForgeProtectedRecordAsync(first); await w.AppendForgeProtectedRecordAsync(next); await w.AppendForgeProtectedRecordAsync(capacity); return true;
        });
        var old = (await Receipt(first))!; var latest = (await Latest(first))!;
        Assert.Equal(2, latest.Revision); Assert.Equal(1, old.Revision); Assert.Equal(0, old.ExpectedRevision);
        Assert.Equal("Synthetic private observation", Open(old).Value); Assert.Equal("Corrected synthetic observation", Open(latest).Value);
        Assert.True(Matches(old, "Synthetic private observation")); Assert.False(Matches(old, "Different synthetic value"));
        Assert.False(Matches(old, "Synthetic private observation", latest.RetryContext));
        Assert.False(Matches(old, "Synthetic private observation", old.ValueContext));
        Assert.NotNull(await Latest(capacity));
        old.Envelope.Ciphertext[0] ^= 1; old.RetryProof.Mac[0] ^= 1;
        Assert.True(Matches((await Receipt(first))!, "Synthetic private observation"));
        Assert.Equal("Synthetic private observation", Open((await Receipt(first))!).Value);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => Append(first))).StatusCode);
        var duplicateRequest = Record(programme, first.Kind, Guid.NewGuid(), request: first.RequestId);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => Append(duplicateRequest))).StatusCode);
        var wrongOwner = first with { OwnerScope = "other-owner", Id = Guid.NewGuid() };
        Assert.Equal(404, (await Assert.ThrowsAsync<DomainException>(() => Append(wrongOwner))).StatusCode);
        var export = await Assert.ThrowsAsync<DomainException>(() => Privacy.ExportAsync(At)); Assert.Equal(503, export.StatusCode);
        var runtimeJson = await services.GetRequiredService<IRuntimeReader>().ReadAsync(s => JsonSerializer.Serialize(s));
        Assert.DoesNotContain("Synthetic private observation", runtimeJson); Assert.DoesNotContain("Synthetic capacity", runtimeJson);
        return (programme, first, next);
    }

    public async Task RollbackAndDelete()
    {
        var (programme, first, latest) = await Versions(); var before = await services.GetRequiredService<IRuntimeReader>().ReadAsync(s => s.AgentControls);
        var sessions = services.GetRequiredService<ISessionStore>();
        var session = await sessions.AddAsync(new(Guid.NewGuid(), ActivityType.MockInterview, "Canonical interview", SessionStatus.Active, null, null, At, At, []));
        var rejected = Record(programme, first.Kind, first.Id, 3);
        await Assert.ThrowsAsync<IOException>(() => Transaction.ExecuteAsync<bool>(async w => {
            await w.AppendForgeProtectedRecordAsync(rejected); w.Runtime.AgentControls = before with { Paused = !before.Paused };
            await w.UpdateSessionAsync(session.Id, s => s with { Title = "Must roll back" });
            await w.SaveForgeProgrammeAsync(programme with { DeletedAt = At, Revision = 2 }); throw new IOException("Synthetic rollback");
        }));
        Assert.Equal(2, (await Latest(first))!.Revision); Assert.Null(await Receipt(rejected));
        Assert.Equal(before, await services.GetRequiredService<IRuntimeReader>().ReadAsync(s => s.AgentControls));
        Assert.Equal(session.Title, (await sessions.GetAsync(session.Id))!.Title);
        await Transaction.ExecuteAsync(async w => {
            await w.AppendForgeProtectedRecordAsync(rejected);
            await w.UpdateSessionAsync(session.Id, s => s with { Title = "Canonical interview retained" });
            await w.SaveForgeProgrammeAsync(programme with { DeletedAt = At, Revision = 2 }); return true;
        });
        Assert.Null(await Latest(first)); Assert.Null(await Receipt(first)); Assert.Null(await Receipt(rejected));
        Assert.Equal(404, (await Assert.ThrowsAsync<DomainException>(() => Append(rejected))).StatusCode);
        Assert.NotNull(Assert.Single((await Privacy.ExportAsync(At)).ForgeProgrammes!).DeletedAt);
        Assert.Equal("Canonical interview retained", (await sessions.GetAsync(session.Id))!.Title);
    }

    public async Task Erasure()
    {
        var (programme, first, _) = await Versions(); var status = await Privacy.StatusAsync();
        var erased = await Privacy.EraseAsync(new(Guid.NewGuid(), status.Revision, PrivacyData.ErasePhrase), new string('a', 64), At);
        Assert.Empty((await Privacy.ExportAsync(At)).ForgeProgrammes ?? []);
        await Privacy.ReopenAsync(new(erased.Revision, PrivacyData.ReopenPhrase), new string('b', 64));
        using (PrivacyContext.Enter(status.Epoch)) Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => Append(first))).StatusCode);
        await Transaction.ExecuteAsync(async w => { await w.SaveForgeProgrammeAsync(programme); return true; });
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => Append(first))).StatusCode);
        Assert.Null(await Latest(first)); Assert.Null(await Receipt(first));
    }
}

public sealed class ForgeProtectedStorageTests
{
    [Fact]
    public async Task ForgeProtected_memory_same_ids_in_capacity_and_checkins_are_isolated() {
        using var host = new ForgeApiFactory(); await new ForgeProtectedStorageFixture(host.Services).KindIsolation();
    }
    [Fact]
    public async Task ForgeProtected_memory_versions_retry_context_and_copy_boundaries() {
        using var host = new ForgeApiFactory(); await new ForgeProtectedStorageFixture(host.Services).Versions();
    }
    [Fact]
    public async Task ForgeProtected_memory_rollback_project_delete_and_export() {
        using var host = new ForgeApiFactory(); await new ForgeProtectedStorageFixture(host.Services).RollbackAndDelete();
    }
    [Fact]
    public async Task ForgeProtected_memory_full_erasure_and_epoch_fence() {
        using var host = new ForgeApiFactory(); await new ForgeProtectedStorageFixture(host.Services).Erasure();
    }
}
