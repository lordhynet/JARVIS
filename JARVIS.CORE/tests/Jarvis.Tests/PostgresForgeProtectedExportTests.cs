using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Forge;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Persistence;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task ForgeProtected_export_postgres_refuses_oversized_result_before_opening_any_payload() {
        await using var database = await TestDatabase.CreateAsync(); using var root = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var host = root.WithWebHostBuilder(ForgeExportFixture.Configure); var rows = await ForgeExportFixture.Seed(host.Services);
        await using var db = await host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>().CreateDbContextAsync();
        // Extra synthetic rows cannot decrypt under changed IDs. Preflight must fail413 before
        // materialization/opening; attempting decryption would instead fail409.
        db.ForgeCheckIns.AddRange(Enumerable.Range(0, ForgeProtectedJson.MaximumExportRevisions - rows.Length + 1)
            .Select(_ => ForgeProtectedRow.From(rows[0] with { Id = Guid.NewGuid(), RequestId = Guid.NewGuid() })));
        await db.SaveChangesAsync();
        Assert.Equal(413, (await Assert.ThrowsAsync<DomainException>(() => host.Services.GetRequiredService<IPrivacyStore>().ExportAsync(ForgeExportFixture.At))).StatusCode);
    }
    [PostgresFact]
    public async Task ForgeProtected_export_postgres_retains_both_kinds_and_every_revision_after_restart() {
        await using var database = await TestDatabase.CreateAsync(); ForgeProtectedRecord[] rows;
        using (var original = new PostgresApiFactory(database.ConnectionString, manualAgents: true))
        using (var host = original.WithWebHostBuilder(ForgeExportFixture.Configure)) {
            rows = await ForgeExportFixture.Seed(host.Services);
            ForgeExportFixture.AssertComplete(await host.Services.GetRequiredService<IPrivacyStore>().ExportAsync(ForgeExportFixture.At), rows);
        }
        using var restartedRoot = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var restarted = restartedRoot.WithWebHostBuilder(ForgeExportFixture.Configure);
        var factory = restarted.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>();
        await using var db = await factory.CreateDbContextAsync();
        var before = (await db.ForgeCheckIns.AsNoTracking().ToArrayAsync()).Select(x => Convert.ToBase64String(x.Ciphertext)).ToArray();
        ForgeExportFixture.AssertComplete(await restarted.Services.GetRequiredService<IPrivacyStore>().ExportAsync(ForgeExportFixture.At), rows);
        Assert.Equal(before, (await db.ForgeCheckIns.AsNoTracking().ToArrayAsync()).Select(x => Convert.ToBase64String(x.Ciphertext)).ToArray());
        Assert.Equal(2, await db.ForgeCapacity.CountAsync());
        var cold = await db.RuntimeDocuments.AsNoTracking().Select(x => x.Json).SingleAsync();
        Assert.DoesNotContain("synthetic-private-export-note", cold);
        var tampered = await db.ForgeCheckIns.SingleAsync(x => x.Revision == 2);
        tampered.Tag = [.. tampered.Tag]; tampered.Tag[0] ^= 1; await db.SaveChangesAsync();
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => restarted.Services.GetRequiredService<IPrivacyStore>().ExportAsync(ForgeExportFixture.At))).StatusCode);
        var privacy = restarted.Services.GetRequiredService<IPrivacyStore>(); var status = await privacy.StatusAsync();
        await privacy.EraseAsync(new(Guid.NewGuid(), status.Revision, PrivacyData.ErasePhrase), new string('a', 64), ForgeExportFixture.At);
        Assert.Null((await privacy.ExportAsync(ForgeExportFixture.At)).ForgeProtectedRecords);
    }

    [PostgresFact]
    public async Task ForgeProtected_export_postgres_concurrent_erasure_cannot_mix_epoch_or_partial_history() {
        await using var database = await TestDatabase.CreateAsync();
        using var writerRoot = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var writer = writerRoot.WithWebHostBuilder(ForgeExportFixture.Configure);
        var rows = await ForgeExportFixture.Seed(writer.Services);
        var card = await writer.Services.GetRequiredService<LearningService>().CreateAsync(new(LearningDomain.French, "Canonical interview practice", "Remains in the snapshot"), default);
        using var blocker = new ExportReadBarrier(ForgeExportFixture.Protector());
        using var readerRoot = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var reader = readerRoot.WithWebHostBuilder(b => {
            ForgeExportFixture.Configure(b);
            b.ConfigureTestServices(s => { s.RemoveAll<IProtectedRecordProtector>(); s.AddSingleton<IProtectedRecordProtector>(blocker); });
        });
        var exportTask = Task.Run(() => reader.Services.GetRequiredService<IPrivacyStore>().ExportAsync(ForgeExportFixture.At));
        try {
            Assert.True(blocker.Entered.Wait(TimeSpan.FromSeconds(20)), "Exporter did not reach the decryption boundary.");
            var privacy = writer.Services.GetRequiredService<IPrivacyStore>(); var status = await privacy.StatusAsync();
            await privacy.EraseAsync(new(Guid.NewGuid(), status.Revision, PrivacyData.ErasePhrase), new string('a', 64), ForgeExportFixture.At);
        } finally { blocker.Continue.Set(); }
        var exported = await exportTask.WaitAsync(TimeSpan.FromSeconds(20));
        ForgeExportFixture.AssertComplete(exported, rows); Assert.Equal(0, exported.Privacy.Epoch);
        Assert.Contains(exported.Learning, x => x.Id == card.Id); Assert.Single(exported.ForgeProgrammes!);
        var after = await writer.Services.GetRequiredService<IPrivacyStore>().ExportAsync(ForgeExportFixture.At);
        Assert.Equal(1, after.Privacy.Epoch); Assert.Null(after.ForgeProtectedRecords); Assert.Empty(after.Learning);
    }

    private sealed class ExportReadBarrier(IProtectedRecordProtector inner) : IProtectedRecordProtector, IDisposable
    {
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Continue { get; } = new();
        public bool Ready => inner.Ready;
        public ProtectedRecordEnvelope Protect(ProtectedRecordContext context, ReadOnlySpan<byte> plaintext) => inner.Protect(context, plaintext);
        public byte[] Unprotect(ProtectedRecordContext context, ProtectedRecordEnvelope envelope) => throw new InvalidOperationException("Borrowed reader required.");
        public T Unprotect<T>(ProtectedRecordContext context, ProtectedRecordEnvelope envelope, Func<ReadOnlySpan<byte>, T> read) {
            Entered.Set();
            if (!Continue.Wait(TimeSpan.FromSeconds(30))) throw new TimeoutException("Synthetic export barrier timed out.");
            return inner.Unprotect(context, envelope, read);
        }
        public ProtectedRetryProof CreateRetryProof(ProtectedRecordContext context, ReadOnlySpan<byte> canonicalRequest) => inner.CreateRetryProof(context, canonicalRequest);
        public bool MatchesRetryProof(ProtectedRecordContext context, ProtectedRetryProof proof, ReadOnlySpan<byte> canonicalRequest) => inner.MatchesRetryProof(context, proof, canonicalRequest);
        public void Dispose() { Entered.Dispose(); Continue.Dispose(); }
    }
}
