using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Cqrs.Imports;
using Jarvis.Orchestrator.Cqrs.Stories;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Persistence;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task Prep_cards_readiness_reordering_matches_story_projection_and_reuses_receipts_without_write_on_replay()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var root = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var host = root.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        using var epoch = PrivacyContext.Enter(0); var store = host.Services.GetRequiredService<IRuntimeStore>(); var clock = new ForgeClock { Now = PrepReadinessSelectionTests.At };
        var csharp = PrepReadinessSelectionTests.Strong("CSharp ready", ".NET thread-pool changes"); var sql = PrepReadinessSelectionTests.Thin("Database draft", "SQL indexing");
        var stories = new StoryCommandHandlers(store, clock);
        await stories.HandleAsync(new CreateStory(new(csharp.Id, csharp.Content))); await stories.HandleAsync(new CreateStory(new(sql.Id, sql.Content)));
        var advert = PrepPackCardSafetyTests.Advert(); var displayed = await PrepPackCardSafetyTests.Seed(store, advert);
        var handler = new PrepPackCardCommands(store, clock); var first = await handler.HandleAsync(new(advert.Id, PrepPackCardSafetyTests.Pick(displayed, 0)));
        await stories.HandleAsync(new UpdateStory(sql.Id, new(1, PrepReadinessSelectionTests.Strong("Database ready", "SQL indexing").Content)));
        var current = await new ImportQueryHandlers(store).HandleAsync(new GetImportPrepPack(advert.Id)); Assert.NotEqual(displayed.Fingerprint, current.Fingerprint);
        var storyReadiness = await new StoryQueryHandlers(store).HandleAsync(new GetStoryReadiness(sql.Id));
        var match = Assert.Single(current.Requirements.Single(r => r.Key == "databases").Stories);
        Assert.Equal((storyReadiness.Score, storyReadiness.Level), (match.Readiness, match.Level));
        var contexts = host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>(); var stamp = await TrainingFootprint.Stamp(contexts);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => handler.HandleAsync(new(advert.Id, PrepPackCardSafetyTests.Pick(displayed, 0))))).StatusCode);
        var repeatedIndex = current.LikelyQuestions.ToList().IndexOf(displayed.LikelyQuestions[0]); Assert.True(repeatedIndex > 0);
        var replay = await handler.HandleAsync(new(advert.Id, PrepPackCardSafetyTests.Pick(current, repeatedIndex)));
        Assert.Equal((0, 1), (replay.Created, replay.Existing)); Assert.Equal(first.Cards[0].Id, Assert.Single(replay.Cards).Id);
        Assert.Equal(stamp, await TrainingFootprint.Stamp(contexts));
    }

    [PostgresFact]
    public async Task Prep_cards_postgres_matches_default_no_write_replay_and_atomic_later_conflict()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var root = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var host = root.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        var store = host.Services.GetRequiredService<IRuntimeStore>(); using var epoch = PrivacyContext.Enter(0);
        var advert = PrepPackCardSafetyTests.Advert(); var pack = await PrepPackCardSafetyTests.Seed(store, advert);
        var memory = new InMemoryRuntimeStore(); memory.ReplacePrivacyData(await store.ReadAsync(s => s));
        var clock = new ForgeClock { Now = PrepPackCardSafetyTests.At }; var command = new CreateImportPrepPackCards(advert.Id, PrepPackCardSafetyTests.Pick(pack, 0, 1));
        var normal = await new PrepPackCardCommands(memory, clock).HandleAsync(command);
        var handler = new PrepPackCardCommands(store, clock); var actual = await handler.HandleAsync(command);
        Assert.Equal((normal.Created, normal.Existing, normal.Retired), (actual.Created, actual.Existing, actual.Retired));
        Assert.Equal(normal.Cards.Select(c => (c.Domain, c.Prompt, c.Answer, c.CreatedAt)), actual.Cards.Select(c => (c.Domain, c.Prompt, c.Answer, c.CreatedAt)));
        Assert.Equal(2, await store.ReadAsync(s => s.CreationReceipts.Count));
        var contexts = host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>(); var stamp = await TrainingFootprint.Stamp(contexts);
        var replay = await handler.HandleAsync(command); Assert.Equal((0, 2, 0), (replay.Created, replay.Existing, replay.Retired));
        Assert.Equal(actual.Cards.Select(c => c.Id), replay.Cards.Select(c => c.Id)); Assert.Equal(stamp, await TrainingFootprint.Stamp(contexts));
        var another = PrepPackCardSafetyTests.Advert(); var otherPack = await PrepPackCardSafetyTests.Seed(store, another); await PrepPackCardSafetyTests.SeedConflict(store, another, otherPack);
        stamp = await TrainingFootprint.Stamp(contexts); var before = await store.ReadAsync(s => JsonSerializer.Serialize(s));
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => handler.HandleAsync(new(another.Id, PrepPackCardSafetyTests.Pick(otherPack, 0, 1))))).StatusCode);
        Assert.Equal(stamp, await TrainingFootprint.Stamp(contexts)); TrainingMigrationFixture.Same(before, await store.ReadAsync(s => JsonSerializer.Serialize(s)));
    }

    [PostgresFact]
    public async Task Prep_cards_two_hosts_report_one_creation_and_one_replay()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var root = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var first = root.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        using var secondRoot = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var second = secondRoot.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        using var epoch = PrivacyContext.Enter(0); var store = first.Services.GetRequiredService<IRuntimeStore>();
        var advert = PrepPackCardSafetyTests.Advert(); var pack = await PrepPackCardSafetyTests.Seed(store, advert);
        var command = new CreateImportPrepPackCards(advert.Id, PrepPackCardSafetyTests.Pick(pack, 0, 1));
        var results = await Task.WhenAll(first.Services.GetRequiredService<PrepPackCardCommands>().HandleAsync(command), second.Services.GetRequiredService<PrepPackCardCommands>().HandleAsync(command));
        Assert.Equal(2, results.Sum(r => r.Created)); Assert.Equal(2, results.Sum(r => r.Existing));
        Assert.Equal(results[0].Cards.Select(c => c.Id), results[1].Cards.Select(c => c.Id)); Assert.Equal(2, await store.ReadAsync(s => s.Learning.Count));
    }

    [PostgresFact]
    public async Task Prep_cards_source_deletion_race_publishes_only_before_the_source_is_retired()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var root = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var host = root.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        using var epoch = PrivacyContext.Enter(0); var store = host.Services.GetRequiredService<IRuntimeStore>();
        var advert = PrepPackCardSafetyTests.Advert(); var pack = await PrepPackCardSafetyTests.Seed(store, advert);
        async Task<object> Create() { try { return await host.Services.GetRequiredService<PrepPackCardCommands>().HandleAsync(new(advert.Id, PrepPackCardSafetyTests.Pick(pack, 0, 1))); } catch (DomainException e) { return e; } }
        var created = Create(); await store.UpdateAsync(s => { ImportService.Purge(s, s.ImportedDocuments[advert.Id], PrepPackCardSafetyTests.At); return true; });
        var outcome = await created;
        if (outcome is DomainException error) { Assert.Equal(404, error.StatusCode); Assert.Empty(await store.ReadAsync(s => s.Learning)); }
        else Assert.Equal(2, Assert.IsType<PrepPackCards>(outcome).Created);
        var handler = host.Services.GetRequiredService<PrepPackCardCommands>();
        Assert.Equal(404, (await Assert.ThrowsAsync<DomainException>(() => handler.HandleAsync(new(advert.Id, PrepPackCardSafetyTests.Pick(pack, 0, 1))))).StatusCode);
    }

    [PostgresTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task Prep_cards_stale_or_erased_store_epoch_refuses_before_publication(bool erased)
    {
        await using var database = await TestDatabase.CreateAsync();
        using var root = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var host = root.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        var store = host.Services.GetRequiredService<IRuntimeStore>(); var advert = PrepPackCardSafetyTests.Advert(); InterviewPrepPack pack;
        using (PrivacyContext.Enter(0)) pack = await PrepPackCardSafetyTests.Seed(store, advert);
        await using var db = SchemaContext(database.ConnectionString); if (erased) await db.Database.ExecuteSqlRawAsync("UPDATE \"PrivacyControls\" SET \"Erased\"=true WHERE \"Id\"=1");
        var contexts = host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>(); var stamp = await TrainingFootprint.Stamp(contexts);
        using (PrivacyContext.Enter(erased ? 0 : 99)) {
            var request = PrepPackCardSafetyTests.Pick(pack, 0) with { DataEpoch = erased ? 0 : 99 };
            Assert.Equal(erased ? 503 : 409, (await Assert.ThrowsAsync<DomainException>(() => host.Services.GetRequiredService<PrepPackCardCommands>().HandleAsync(new(advert.Id, request)))).StatusCode);
        }
        Assert.Equal(stamp, await TrainingFootprint.Stamp(contexts));
    }

    [PostgresTheory]
    [InlineData(false)] [InlineData(true)]
    public async Task Prep_cards_post_save_failure_or_cancellation_rolls_back_cards_and_receipts(bool cancel)
    {
        await using var database = await TestDatabase.CreateAsync(); var failure = new PrepCardSaveFailure();
        using var root = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var host = root.WithWebHostBuilder(b => b.ConfigureTestServices(s => {
            s.RemoveAll<IHostedService>(); s.RemoveAll<IDbContextFactory<SessionDbContext>>();
            s.AddSingleton<IDbContextFactory<SessionDbContext>>(new PrepCardContextFactory(database.ConnectionString, failure));
        }));
        using var epoch = PrivacyContext.Enter(0); var store = host.Services.GetRequiredService<IRuntimeStore>(); var advert = PrepPackCardSafetyTests.Advert();
        var pack = await PrepPackCardSafetyTests.Seed(store, advert); var contexts = host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>(); var stamp = await TrainingFootprint.Stamp(contexts);
        var command = new CreateImportPrepPackCards(advert.Id, PrepPackCardSafetyTests.Pick(pack, 0, 1));
        using var cancellation = new CancellationTokenSource(); failure.Enabled = true; if (cancel) failure.CancelAfterSave = cancellation;
        var handler = host.Services.GetRequiredService<PrepPackCardCommands>();
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.HandleAsync(command, cancellation.Token));
        else await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(command, cancellation.Token));
        Assert.Equal(1, failure.Failures); Assert.Equal(stamp, await TrainingFootprint.Stamp(contexts));
        Assert.Empty(await store.ReadAsync(s => s.Learning)); Assert.Empty(await store.ReadAsync(s => s.CreationReceipts));
    }

    private sealed class PrepCardSaveFailure : SaveChangesInterceptor
    {
        public bool Enabled { get; set; } public int Failures { get; private set; } public CancellationTokenSource? CancelAfterSave { get; set; }
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken ct = default) {
            if (Enabled) { Failures++; CancelAfterSave?.Cancel(); ct.ThrowIfCancellationRequested(); throw new InvalidOperationException("Synthetic prep-card post-save failure."); } return ValueTask.FromResult(result);
        }
    }
    private sealed class PrepCardContextFactory(string connection, PrepCardSaveFailure failure) : IDbContextFactory<SessionDbContext>
    {
        public SessionDbContext CreateDbContext() => new(new DbContextOptionsBuilder<SessionDbContext>().UseNpgsql(connection).AddInterceptors(failure).Options);
        public Task<SessionDbContext> CreateDbContextAsync(CancellationToken ct = default) => Task.FromResult(CreateDbContext());
    }
}
