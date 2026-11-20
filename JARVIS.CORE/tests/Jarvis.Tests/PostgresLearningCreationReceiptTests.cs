using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Recordings;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Persistence;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task Creation_receipts_postgres_two_hosts_race_and_original_reply_survives_restart_correction_and_removal() {
        await using var database = await TestDatabase.CreateAsync(); var request = LearningCreationReceiptTests.Request(); LearningItem original;
        using (var firstRoot = new PostgresApiFactory(database.ConnectionString, manualAgents: true))
        using (var first = firstRoot.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>())))
        using (var secondRoot = new PostgresApiFactory(database.ConnectionString, manualAgents: true))
        using (var second = secondRoot.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()))) {
            var clock = new CreationReceiptClock(); var otherClock = new CreationReceiptClock();
            var a = new LearningService(first.Services.GetRequiredService<IRuntimeStore>(), clock);
            var b = new LearningService(second.Services.GetRequiredService<IRuntimeStore>(), otherClock);
            using var epoch = PrivacyContext.Enter(0);
            var replies = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => (i % 2 == 0 ? a : b).CreateAsync(request, default)));
            original = replies[0]; Assert.All(replies, x => Assert.Equal(JsonSerializer.Serialize(original), JsonSerializer.Serialize(x)));
            Assert.Equal(1, clock.Calls + otherClock.Calls);
            await first.Services.GetRequiredService<IRuntimeStore>().UpdateAsync(s => { s.Learning[original.Id] = original with { Answer = "Corrected answer" }; return true; });
        }
        using var restartedRoot = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var restarted = restartedRoot.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        var store = restarted.Services.GetRequiredService<IRuntimeStore>(); var replayClock = new CreationReceiptClock(); var service = new LearningService(store, replayClock);
        using var current = PrivacyContext.Enter(0);
        Assert.Equal(original, await service.CreateAsync(request with { Prompt = "Synthetic prompt", Answer = "Synthetic answer" }, default));
        Assert.Equal("Corrected answer", await store.ReadAsync(s => s.Learning[original.Id].Answer));
        await store.UpdateAsync(s => s.Learning.Remove(original.Id)); Assert.Equal(original, await service.CreateAsync(request, default));
        Assert.Null(await store.ReadLearningItemAsync(original.Id)); Assert.Equal(0, replayClock.Calls);
        var export = await restarted.Services.GetRequiredService<IPrivacyStore>().ExportAsync(DateTimeOffset.UtcNow);
        Assert.Equal(original, Assert.Single(export.CreationReceipts!).Result); Assert.DoesNotContain("Fingerprint", JsonSerializer.Serialize(export.CreationReceipts));
    }

    [PostgresFact]
    public async Task Creation_receipts_postgres_scoped_async_path_ignores_unrelated_rows_and_replay_does_not_write() {
        await using var database = await TestDatabase.CreateAsync(); using var root = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var host = root.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        var capture = new CreationCapture(); var factory = new CreationContextFactory(database.ConnectionString, capture);
        var store = new PostgresRuntimeStore(factory); var service = new LearningService(store, new CreationReceiptClock()); var request = LearningCreationReceiptTests.Request();
        await using (var db = await factory.CreateDbContextAsync()) {
            await db.Database.MigrateAsync();
            db.RuntimeLearningRows.Add(new() { Kind = "Reviews", Id = Guid.NewGuid(), Json = "null" }); await db.SaveChangesAsync();
        }
        async Task<string> Stamp() { await using var db = await factory.CreateDbContextAsync(); return await db.Database.SqlQueryRaw<string>("""SELECT xmin::text AS "Value" FROM "RuntimeDocuments" WHERE "Id"=1""").SingleAsync(); }
        var before = await Stamp(); using var epoch = PrivacyContext.Enter(0); capture.Enabled = true;
        var first = await service.CreateAsync(request, default); capture.Enabled = false;
        Assert.DoesNotContain(capture.Commands, x => x.Contains("RuntimeAudioState", StringComparison.Ordinal) || x.Contains("RuntimeLearningState", StringComparison.Ordinal));
        Assert.DoesNotContain(capture.Commands, x => x.Contains("RuntimeDocuments", StringComparison.Ordinal) && x.Contains("\"Json\"", StringComparison.Ordinal));
        Assert.Equal(before, await Stamp()); capture.Commands.Clear(); capture.Enabled = true;
        Assert.Equal(first, await service.CreateAsync(request, default)); capture.Enabled = false;
        Assert.DoesNotContain(capture.Commands, x => x.TrimStart().StartsWith("INSERT", StringComparison.Ordinal) || x.TrimStart().StartsWith("UPDATE", StringComparison.Ordinal) || x.TrimStart().StartsWith("DELETE", StringComparison.Ordinal));
        Assert.Single(capture.Commands, x => x.Contains("RuntimeLearningRows", StringComparison.Ordinal));
        using (PrivacyContext.Enter(1)) Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => service.CreateAsync(request, default))).StatusCode);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.CreateAsync(request with { RequestId = Guid.NewGuid() }, cancelled.Token));
    }

    [PostgresFact]
    public async Task Creation_receipts_postgres_failure_after_save_rolls_back_card_and_receipt_together() {
        await using var database = await TestDatabase.CreateAsync(); using var root = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var host = root.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        var regular = host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>();
        var failing = new CreationFailureFactory(database.ConnectionString); var request = LearningCreationReceiptTests.Request(); using var epoch = PrivacyContext.Enter(0);
        await Assert.ThrowsAsync<IOException>(() => new LearningService(new PostgresRuntimeStore(failing), new CreationReceiptClock()).CreateAsync(request, default));
        await using var db = await regular.CreateDbContextAsync(); Assert.Equal(0, await db.RuntimeLearningRows.CountAsync());
        var card = await new LearningService(new PostgresRuntimeStore(regular), new CreationReceiptClock()).CreateAsync(request, default);
        Assert.Equal(2, await db.RuntimeLearningRows.CountAsync()); Assert.Equal(card.Id, (await db.RuntimeLearningRows.SingleAsync(x => x.Kind == "Learning")).Id);
    }

    [PostgresFact]
    public async Task Creation_receipts_postgres_recording_privacy_retirement_is_atomic_and_fences_original_words() {
        await using var database = await TestDatabase.CreateAsync(); using var root = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var host = root.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        var service = host.Services.GetRequiredService<LearningService>(); var transaction = host.Services.GetRequiredService<ICoreTransaction>();
        var request = LearningCreationReceiptTests.Request() with { Prompt = "Private recording fixture", Answer = "Captured words must disappear" };
        using var epoch = PrivacyContext.Enter(0); var card = await service.CreateAsync(request, default); var recording = Guid.NewGuid();
        await transaction.ExecuteAsync(w => { var s = w.Runtime; s.Learning[card.Id] = card with { SourceRecordingId = recording };
            s.CreationReceipts[request.RequestId!.Value] = s.CreationReceipts[request.RequestId.Value] with { Result = card with { SourceRecordingId = recording } }; return Task.FromResult(true); });
        await Assert.ThrowsAsync<IOException>(() => transaction.ExecuteAsync<bool>(w => { RecordedPracticePrivacy.Purge(w.Runtime, recording, DateTimeOffset.UtcNow); throw new IOException("Synthetic privacy rollback"); }));
        Assert.Equal(LearningCreationReceipt.Created, await host.Services.GetRequiredService<IRuntimeReader>().ReadAsync(s => s.CreationReceipts[request.RequestId!.Value].Status));
        await transaction.ExecuteAsync(w => { RecordedPracticePrivacy.Purge(w.Runtime, recording, DateTimeOffset.UtcNow); return Task.FromResult(true); });
        var error = await Assert.ThrowsAsync<DomainException>(() => service.CreateAsync(request, default)); Assert.Equal(410, error.StatusCode);
        var export = await host.Services.GetRequiredService<IPrivacyStore>().ExportAsync(DateTimeOffset.UtcNow); var retired = Assert.Single(export.CreationReceipts!);
        Assert.Null(retired.Result); Assert.Null(retired.CardId); Assert.NotNull(retired.RetiredAt);
        await using var db = await host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>().CreateDbContextAsync();
        var json = (await db.RuntimeLearningRows.SingleAsync(x => x.Kind == nameof(RuntimeState.CreationReceipts))).Json;
        Assert.DoesNotContain("Private recording fixture", json); Assert.DoesNotContain("Captured words", json); Assert.DoesNotContain("Fingerprint", json);
        var status = await host.Services.GetRequiredService<IPrivacyStore>().StatusAsync();
        await host.Services.GetRequiredService<IPrivacyStore>().EraseAsync(new(Guid.NewGuid(), status.Revision, PrivacyData.ErasePhrase), new string('a', 64), DateTimeOffset.UtcNow);
        using (PrivacyContext.Enter(1)) Assert.Null((await host.Services.GetRequiredService<IPrivacyStore>().ExportAsync(DateTimeOffset.UtcNow)).CreationReceipts);
    }

    [PostgresFact]
    public async Task Creation_receipts_marker_down_refuses_loss_and_empty_roundtrip_preserves_existing_card() {
        await using var database = await TestDatabase.CreateAsync(); using var root = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var host = root.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        var request = LearningCreationReceiptTests.Request(); using var epoch = PrivacyContext.Enter(0);
        var card = await host.Services.GetRequiredService<LearningService>().CreateAsync(request, default);
        await using var db = await host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>().CreateDbContextAsync();
        var migrator = db.Database.GetService<IMigrator>();
        var error = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync("20261120161309_LearningReviewCardIndex"));
        Assert.Contains("Refusing creation-receipt downgrade", error.MessageText);
        Assert.Equal(2, await db.RuntimeLearningRows.CountAsync()); Assert.Contains("20261120171901_LearningCreationReceipts", await db.Database.GetAppliedMigrationsAsync());
        await db.RuntimeLearningRows.Where(x => x.Kind == nameof(RuntimeState.CreationReceipts)).ExecuteDeleteAsync();
        var before = (await db.RuntimeLearningRows.SingleAsync()).Json;
        await migrator.MigrateAsync("20261120161309_LearningReviewCardIndex"); await db.Database.MigrateAsync();
        Assert.Equal(before, (await db.RuntimeLearningRows.AsNoTracking().SingleAsync()).Json);
        Assert.Equal(card, await host.Services.GetRequiredService<IRuntimeReader>().ReadLearningItemAsync(card.Id));
    }

    private sealed class CreationFailureFactory(string connection) : IDbContextFactory<SessionDbContext>
    {
        public SessionDbContext CreateDbContext() => new(new DbContextOptionsBuilder<SessionDbContext>().UseNpgsql(connection).AddInterceptors(new CreationSavedFailure()).Options);
        public Task<SessionDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }
    private sealed class CreationSavedFailure : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken ct = default) => throw new IOException("Synthetic failure after SQL save before commit");
    }
}
