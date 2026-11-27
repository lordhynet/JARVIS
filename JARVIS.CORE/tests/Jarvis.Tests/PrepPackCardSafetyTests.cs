using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Coaching;
using Jarvis.Orchestrator.Cqrs.Imports;
using Jarvis.Orchestrator.Domains;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Tests;

public sealed class PrepPackCardSafetyTests
{
    internal static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-27T06:50:00Z");
    internal static ImportedDocument Advert() => new(Guid.NewGuid(), "job-advert", "Synthetic role", "C# and .NET with SQL", "fixture-hash", 1, At, []);
    internal static CreatePrepPackCardsRequest Pick(InterviewPrepPack pack, params int[] indexes) => new(indexes, pack.Fingerprint, pack.DataEpoch);
    internal static async Task<InterviewPrepPack> Seed(IRuntimeStore store, ImportedDocument advert)
    {
        await store.UpdateAsync(s => { s.ImportedDocuments[advert.Id] = advert; return true; });
        return await new ImportQueryHandlers(store).HandleAsync(new GetImportPrepPack(advert.Id));
    }

    [Fact]
    public async Task Source_is_revalidated_inside_write_instead_of_publishing_an_earlier_read()
    {
        IRuntimeStore inner = new InMemoryRuntimeStore(); var advert = Advert(); using var epoch = PrivacyContext.Enter(0);
        var pack = await Seed(inner, advert);
        var store = new BeforeWriteStore(inner, () => inner.UpdateAsync(s => { ImportService.Purge(s, s.ImportedDocuments[advert.Id], At); return true; }));
        var handler = new PrepPackCardCommands(store, TimeProvider.System);
        Assert.Equal(404, (await Assert.ThrowsAsync<DomainException>(() => handler.HandleAsync(new(advert.Id, Pick(pack, 0))))).StatusCode);
        Assert.Equal(0, store.Reads); Assert.Empty(await inner.ReadAsync(s => s.Learning));
    }

    [Fact]
    public async Task Later_conflict_rolls_back_entire_batch()
    {
        IRuntimeStore store = new InMemoryRuntimeStore(); var advert = Advert(); using var epoch = PrivacyContext.Enter(0); var pack = await Seed(store, advert);
        await SeedConflict(store, advert, pack);
        var before = await store.ReadAsync(s => JsonSerializer.Serialize(s));
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => new PrepPackCardCommands(store, TimeProvider.System).HandleAsync(new(advert.Id, Pick(pack, 0, 1))))).StatusCode);
        TrainingMigrationFixture.Same(before, await store.ReadAsync(s => JsonSerializer.Serialize(s)));
    }

    internal static Task<LearningItem> SeedConflict(IRuntimeStore store, ImportedDocument advert, InterviewPrepPack pack)
    {
        var id = DomainPolicy.StableId("prep-pack-card:v1:" + advert.Id.ToString("N") + ":" + pack.LikelyQuestions[1]);
        return new LearningService(store, TimeProvider.System).CreateAsync(new(LearningDomain.InterviewStory, "Conflicting existing question", "Existing guide", id), default);
    }

    [Fact]
    public async Task Changed_gap_order_rejects_old_indexes_instead_of_creating_different_cards()
    {
        IRuntimeStore store = new InMemoryRuntimeStore(); var advert = Advert(); using var epoch = PrivacyContext.Enter(0); var displayed = await Seed(store, advert);
        await store.UpdateAsync(s => { var id = Guid.NewGuid(); s.Stories[id] = new(id, new StoryContent("C# ownership", "Fixture", "Fixture", TechnicalDecisions: ".NET"), 1, At, At); return true; });
        var current = await new ImportQueryHandlers(store).HandleAsync(new GetImportPrepPack(advert.Id)); Assert.NotEqual(displayed.LikelyQuestions[0], current.LikelyQuestions[0]);
        var handler = new PrepPackCardCommands(store, TimeProvider.System);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => handler.HandleAsync(new(advert.Id, Pick(displayed, 0))))).StatusCode);
        Assert.Empty(await store.ReadAsync(s => s.Learning));
        Assert.Equal(current.LikelyQuestions[0], Assert.Single((await handler.HandleAsync(new(advert.Id, Pick(current, 0)))).Cards).Prompt);
    }

    [Fact]
    public async Task Concurrent_requests_report_honest_creation_and_replay_counts()
    {
        IRuntimeStore store = new InMemoryRuntimeStore(); var advert = Advert(); using var epoch = PrivacyContext.Enter(0); var pack = await Seed(store, advert);
        var handler = new PrepPackCardCommands(store, TimeProvider.System); var command = new CreateImportPrepPackCards(advert.Id, Pick(pack, 0, 1));
        var results = await Task.WhenAll(Task.Run(() => handler.HandleAsync(command)), Task.Run(() => handler.HandleAsync(command)));
        Assert.Equal(2, results.Sum(r => r.Created)); Assert.Equal(2, results.Sum(r => r.Existing));
        Assert.Equal(results[0].Cards.Select(c => c.Id), results[1].Cards.Select(c => c.Id));
        Assert.Equal(2, await store.ReadAsync(s => s.Learning.Count));
    }

    [Fact]
    public async Task Removed_cards_are_not_resurrected_and_retired_receipts_return_no_content()
    {
        IRuntimeStore store = new InMemoryRuntimeStore(); var advert = Advert(); using var epoch = PrivacyContext.Enter(0); var pack = await Seed(store, advert);
        var handler = new PrepPackCardCommands(store, TimeProvider.System); var command = new CreateImportPrepPackCards(advert.Id, Pick(pack, 0, 1));
        var first = await handler.HandleAsync(command);
        await store.UpdateAsync(s => { LearningCreationReceipt.Retire(s, new HashSet<Guid> { first.Cards[0].Id }, At); s.Learning.Clear(); return true; });
        var replay = await handler.HandleAsync(command); Assert.Equal((0, 1, 1), (replay.Created, replay.Existing, replay.Retired));
        Assert.Equal(first.Cards[1].Id, Assert.Single(replay.Cards).Id); Assert.Empty(await store.ReadAsync(s => s.Learning));
        // Generic catalogue cards already created belong to Learning; deletion prevents further creation/replay.
        await store.UpdateAsync(s => { ImportService.Purge(s, s.ImportedDocuments[advert.Id], At); return true; });
        Assert.Equal(404, (await Assert.ThrowsAsync<DomainException>(() => handler.HandleAsync(command))).StatusCode);
    }

    [Theory]
    [InlineData("missing-epoch", 400)] [InlineData("stale-epoch", 409)] [InlineData("missing-fingerprint", 400)] [InlineData("stale-fingerprint", 409)]
    public async Task Explicit_displayed_generation_and_pack_are_required(string scenario, int status)
    {
        IRuntimeStore store = new InMemoryRuntimeStore(); var advert = Advert(); using var epoch = PrivacyContext.Enter(0); var pack = await Seed(store, advert);
        var request = Pick(pack, 0);
        request = scenario switch { "missing-epoch" => request with { DataEpoch = null }, "stale-epoch" => request with { DataEpoch = 99 },
            "missing-fingerprint" => request with { ExpectedPackFingerprint = null }, _ => request with { ExpectedPackFingerprint = new string('0', 64) } };
        Assert.Equal(status, (await Assert.ThrowsAsync<DomainException>(() => new PrepPackCardCommands(store, TimeProvider.System).HandleAsync(new(advert.Id, request)))).StatusCode);
        Assert.Empty(await store.ReadAsync(s => s.Learning));
    }

    [Fact]
    public async Task Cancellation_during_second_decision_publishes_no_cards_or_receipts()
    {
        IRuntimeStore store = new InMemoryRuntimeStore(); var advert = Advert(); using var epoch = PrivacyContext.Enter(0); var pack = await Seed(store, advert);
        using var cancellation = new CancellationTokenSource(); var clock = new CancelClock(cancellation);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new PrepPackCardCommands(store, clock).HandleAsync(new(advert.Id, Pick(pack, 0, 1)), cancellation.Token));
        Assert.Empty(await store.ReadAsync(s => s.Learning)); Assert.Empty(await store.ReadAsync(s => s.CreationReceipts));
    }

    [Fact]
    public async Task Http_endpoint_requires_explicit_body_epoch_and_fingerprint()
    {
        using var host = new JarvisApiFactory(); using var client = host.CreateAuthorisedClient();
        var advert = await LifeScenarioTests.Post<ImportedDocument>(client, "/api/imports", new ImportDocumentRequest(Guid.NewGuid(), "job-advert", "Synthetic role", "SQL", true));
        using (var missing = await client.PostAsJsonAsync($"/api/imports/{advert.Id}/prep-pack/cards", new CreatePrepPackCardsRequest([0]))) Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        var pack = (await client.GetFromJsonAsync<InterviewPrepPack>($"/api/imports/{advert.Id}/prep-pack", LifeScenarioTests.Json))!;
        Assert.Equal(0, pack.DataEpoch); Assert.Equal(64, pack.Fingerprint!.Length);
        using (var stale = await client.PostAsJsonAsync($"/api/imports/{advert.Id}/prep-pack/cards", Pick(pack, 0) with { DataEpoch = 9 })) Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using var anonymous = host.CreateClient(); using var refused = await anonymous.PostAsJsonAsync($"/api/imports/{advert.Id}/prep-pack/cards", Pick(pack, 0)); Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    private sealed class CancelClock(CancellationTokenSource cancellation) : TimeProvider
    {
        private int calls;
        public override DateTimeOffset GetUtcNow() { if (++calls == 2) cancellation.Cancel(); return At; }
    }
    private sealed class BeforeWriteStore(IRuntimeStore inner, Func<Task> before) : IRuntimeStore
    {
        public int Reads { get; private set; }
        public Task<T> ReadAsync<T>(Func<RuntimeState, T> read, CancellationToken ct = default) { Reads++; return inner.ReadAsync(read, ct); }
        public async Task<T> UpdateAsync<T>(Func<RuntimeState, T> update, CancellationToken ct = default) { await before(); return await inner.UpdateAsync(update, ct); }
    }
}
