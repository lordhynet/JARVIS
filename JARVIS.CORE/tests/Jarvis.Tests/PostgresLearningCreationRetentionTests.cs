using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Persistence;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task Creation_receipt_retention_postgres_rollback_race_and_restart_preserve_tombstone_and_canonical_history() {
        await using var database = await TestDatabase.CreateAsync(); using var root = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var host = root.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        var store = host.Services.GetRequiredService<IRuntimeStore>(); var privacy = host.Services.GetRequiredService<IPrivacyStore>();
        using var epoch = PrivacyContext.Enter(0); var at = DateTimeOffset.Parse("2026-12-21T12:00:00Z");
        var clock = new CreationReceiptClock { Now = at.AddDays(-30).AddTicks(-10) }; var service = new LearningService(store, clock);
        var request = LearningCreationReceiptTests.Request() with { Prompt = "Old original PG prompt", Answer = "Old original PG answer" };
        var old = await service.CreateAsync(request, default); var corrected = old with { Prompt = "Current canonical prompt", Answer = "Current canonical answer" };
        await store.UpdateAsync(s => { s.Learning[old.Id] = corrected; return true; });
        await service.ReviewAsync(old.Id, new(Guid.NewGuid(), ReviewRating.Good, 2), default);
        var current = await store.ReadLearningItemAsync(old.Id); var history = JsonSerializer.Serialize(await store.ReadAsync(s => s.Reviews));
        clock.Now = at.AddDays(-30); var edgeRequest = LearningCreationReceiptTests.Request(); var edge = await service.CreateAsync(edgeRequest, default);
        var policy = new RetentionPolicy(); var preview = await privacy.PreviewAsync(policy, at); Assert.Equal(1, preview.Eligible.CreationReceipts);
        var apply = new ApplyRetentionRequest(Guid.NewGuid(), at, preview.PolicyFingerprint, PrivacyData.RetentionPhrase);
        var failing = new PostgresPrivacyStore(new CreationFailureFactory(database.ConnectionString), new AudioFenceChanges());
        await Assert.ThrowsAsync<IOException>(() => failing.RetainAsync(apply, policy, at));
        Assert.Equal(old, await service.CreateAsync(request, default)); Assert.Empty(await privacy.JobsAsync());
        Assert.Equal(1, (await privacy.PreviewAsync(policy, at)).Eligible.CreationReceipts);
        using var otherRoot = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var other = otherRoot.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        var replayer = new LearningService(other.Services.GetRequiredService<IRuntimeStore>(), new CreationReceiptClock());
        // Both transaction orderings are valid: original result before retirement,410 after it.
        var retries = Enumerable.Range(0, 8).Select(async _ => {
            try { Assert.Equal(old, await replayer.CreateAsync(request, default)); }
            catch (DomainException error) { Assert.Equal(410, error.StatusCode); Assert.Equal("learning_creation_retired", error.Code); }
        }).ToArray();
        var job = await privacy.RetainAsync(apply, policy, at); await Task.WhenAll(retries); Assert.Equal(1, job.Removed.CreationReceipts);
        Assert.Equal(current, await store.ReadLearningItemAsync(old.Id)); Assert.Equal(history, JsonSerializer.Serialize(await store.ReadAsync(s => s.Reviews)));
        using var restartedRoot = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var restarted = restartedRoot.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        var restored = restarted.Services.GetRequiredService<IPrivacyStore>(); var replay = restarted.Services.GetRequiredService<LearningService>();
        Assert.Equal(job, await restored.RetainAsync(apply, policy, at.AddMinutes(1)));
        var error = await Assert.ThrowsAsync<DomainException>(() => replay.CreateAsync(request with { Prompt = "Different input" }, default));
        Assert.Equal(410, error.StatusCode); Assert.Equal(edge, await replay.CreateAsync(edgeRequest, default));
        var dump = JsonSerializer.Serialize(await restored.ExportAsync(at)); Assert.DoesNotContain("Old original PG", dump);
        await using var db = await restarted.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>().CreateDbContextAsync();
        var receiptJson = (await db.RuntimeLearningRows.AsNoTracking().SingleAsync(x => x.Kind == "CreationReceipts" && x.Id == request.RequestId)).Json;
        Assert.DoesNotContain("Fingerprint", receiptJson); using var parsed = JsonDocument.Parse(receiptJson);
        Assert.Equal(4, parsed.RootElement.EnumerateObject().Count());
        Assert.Equal(0, (await restored.PreviewAsync(policy, at)).Eligible.CreationReceipts);
        var repeat = await restored.RetainAsync(apply with { RequestId = Guid.NewGuid() }, policy, at); Assert.Equal(0, repeat.Removed.CreationReceipts);
        Assert.Equal(at, Assert.Single((await restored.ExportAsync(at)).CreationReceipts!, x => x.RequestId == request.RequestId).RetiredAt);
        var status = await restored.StatusAsync(); await restored.EraseAsync(new(Guid.NewGuid(), status.Revision, PrivacyData.ErasePhrase), new string('a', 64), at);
        using var erasedEpoch = PrivacyContext.Enter(1); Assert.Equal(2, Assert.Single(await restored.JobsAsync()).Removed.CreationReceipts);
    }
}
