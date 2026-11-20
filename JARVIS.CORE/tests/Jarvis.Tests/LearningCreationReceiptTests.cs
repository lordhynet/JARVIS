using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Recordings;
using Jarvis.Orchestrator.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

internal sealed class CreationReceiptClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.Parse("2026-11-20T17:00:00Z").AddTicks(7).ToOffset(TimeSpan.FromHours(5.5));
    public int Calls { get; private set; }
    public override DateTimeOffset GetUtcNow() { Calls++; return Now; }
}

public sealed class LearningCreationReceiptTests
{
    internal static CreateLearningRequest Request() => new(LearningDomain.French, "  Synthetic prompt  ", "  Synthetic answer  ", Guid.NewGuid());

    [Fact]
    public async Task Creation_receipts_bind_the_actual_nonzero_privacy_epoch() {
        var boundary = new MemoryPrivacyBoundary { Status = new(9, 1, false, null, null) };
        IRuntimeStore store = new GuardedMemoryRuntime(new InMemoryRuntimeStore(), boundary);
        var service = new LearningService(store, new CreationReceiptClock()); var request = Request();
        using (PrivacyContext.Enter(9)) {
            var card = await service.CreateAsync(request, default); Assert.Equal(card, await service.CreateAsync(request, default));
            Assert.Equal(9, await store.ReadAsync(s => s.CreationReceipts[request.RequestId!.Value].Epoch));
        }
        using (PrivacyContext.Enter(8)) Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => service.CreateAsync(request, default))).StatusCode);
    }

    [Fact]
    public async Task Creation_receipts_preserve_original_reply_after_correction_and_card_removal_without_resurrection() {
        IRuntimeStore store = new InMemoryRuntimeStore(); var clock = new CreationReceiptClock(); var service = new LearningService(store, clock); var request = Request();
        using var epoch = PrivacyContext.Enter(0); var first = await service.CreateAsync(request, default);
        Assert.Equal("Synthetic prompt", first.Prompt); Assert.Equal("Synthetic answer", first.Answer);
        await store.UpdateAsync(s => { s.Learning[first.Id] = first with { Answer = "Corrected current answer", Stability = 15 }; return true; });
        clock.Now = clock.Now.AddDays(10);
        var retry = await service.CreateAsync(request with { Prompt = "Synthetic prompt", Answer = "Synthetic answer" }, default);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(retry)); Assert.Equal(1, clock.Calls);
        Assert.Equal("Corrected current answer", await store.ReadAsync(s => s.Learning[first.Id].Answer));
        await store.UpdateAsync(s => s.Learning.Remove(first.Id));
        Assert.Equal(first, await service.CreateAsync(request, default)); Assert.Empty(await store.ReadAsync(s => s.Learning));
        Assert.Single(await store.ReadAsync(s => s.CreationReceipts)); Assert.Equal(1, clock.Calls);
    }

    [Theory]
    [InlineData("domain")]
    [InlineData("prompt")]
    [InlineData("answer")]
    public async Task Creation_receipts_changed_content_conflicts_without_creating_a_second_card(string field) {
        IRuntimeStore store = new InMemoryRuntimeStore(); var clock = new CreationReceiptClock(); var service = new LearningService(store, clock); var request = Request();
        using var epoch = PrivacyContext.Enter(0); var first = await service.CreateAsync(request, default);
        var changed = field switch { "domain" => request with { Domain = LearningDomain.CSharpDotNet }, "prompt" => request with { Prompt = "Changed" }, _ => request with { Answer = "Changed" } };
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => service.CreateAsync(changed, default))).StatusCode);
        Assert.Equal(first, Assert.Single(await store.ReadAsync(s => s.Learning.Values.ToArray()))); Assert.Equal(1, clock.Calls);
    }

    [Fact]
    public async Task Creation_receipts_require_identity_and_epoch_but_legacy_no_id_creates_remain_distinct() {
        IRuntimeStore store = new InMemoryRuntimeStore(); var service = new LearningService(store, new CreationReceiptClock()); var request = Request();
        Assert.Equal(400, (await Assert.ThrowsAsync<DomainException>(() => service.CreateAsync(request, default))).StatusCode);
        using var epoch = PrivacyContext.Enter(0);
        Assert.Equal(400, (await Assert.ThrowsAsync<DomainException>(() => service.CreateAsync(request with { RequestId = Guid.Empty }, default))).StatusCode);
        var legacy = request with { RequestId = null };
        Assert.DoesNotContain("RequestId", JsonSerializer.Serialize(legacy));
        Assert.NotEqual((await service.CreateAsync(legacy, default)).Id, (await service.CreateAsync(legacy, default)).Id);
        Assert.Empty(await store.ReadAsync(s => s.CreationReceipts));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Creation_receipts_privacy_deletion_retires_source_or_question_lineage_and_removes_words(bool questionOnly) {
        IRuntimeStore store = new InMemoryRuntimeStore(); var service = new LearningService(store, new CreationReceiptClock());
        var request = Request() with { Prompt = "Private recording words", Answer = "Private captured answer" };
        using var epoch = PrivacyContext.Enter(0); var card = await service.CreateAsync(request, default); var recording = Guid.NewGuid(); var question = Guid.NewGuid();
        await store.UpdateAsync(s => {
            var receipt = s.CreationReceipts[request.RequestId!.Value];
            s.CreationReceipts[receipt.RequestId] = receipt with { Result = card with { SourceRecordingId = questionOnly ? null : recording, RecordedQuestionId = question } };
            s.Learning.Remove(card.Id); // Still retire an original reply after the current card is gone.
            if (questionOnly) { var linked = card with { Id = Guid.NewGuid(), SourceRecordingId = recording, RecordedQuestionId = question }; s.Learning.Add(linked.Id, linked); }
            RecordedPracticePrivacy.Purge(s, recording, DateTimeOffset.UtcNow); return true;
        });
        var tombstone = await store.ReadAsync(s => s.CreationReceipts[request.RequestId!.Value]); tombstone.Validate();
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(tombstone));
        Assert.Equal(new[] { "Epoch", "RequestId", "RetiredAt", "Status" }, json.RootElement.EnumerateObject().Select(x => x.Name).Order().ToArray());
        var error = await Assert.ThrowsAsync<DomainException>(() => service.CreateAsync(request with { Answer = "Changed retry" }, default));
        Assert.Equal(410, error.StatusCode); Assert.Equal("learning_creation_retired", error.Code);
        var dump = await store.ReadAsync(s => JsonSerializer.Serialize(s));
        Assert.DoesNotContain("Private recording words", dump); Assert.DoesNotContain("Private captured answer", dump);
        Assert.DoesNotContain("Fingerprint", JsonSerializer.Serialize(tombstone.Export())); Assert.Null(tombstone.Export().CardId);
    }

    [Fact]
    public async Task Creation_receipts_http_require_explicit_epoch_and_export_erasure_remain_complete() {
        using var host = new ForgeApiFactory(); using var owner = host.Owner(); var request = Request();
        using var missing = await owner.PostAsJsonAsync("/api/learning", request); Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        owner.DefaultRequestHeaders.Add("X-Jarvis-Data-Epoch", "0");
        using var created = await owner.PostAsJsonAsync("/api/learning", request); created.EnsureSuccessStatusCode();
        var first = (await created.Content.ReadFromJsonAsync<LearningItem>(LifeScenarioTests.Json))!;
        using var repeated = await owner.PostAsJsonAsync("/api/learning", request); repeated.EnsureSuccessStatusCode();
        Assert.Equal(first, await repeated.Content.ReadFromJsonAsync<LearningItem>(LifeScenarioTests.Json));
        var export = (await owner.GetFromJsonAsync<CoreDataExport>("/api/privacy/export", LifeScenarioTests.Json))!;
        var receipt = Assert.Single(export.CreationReceipts!); Assert.Equal(first, receipt.Result); Assert.Equal(first.CreatedAt, receipt.CreatedAt); Assert.Equal(first.Id, receipt.CardId);
        Assert.DoesNotContain("Fingerprint", JsonSerializer.Serialize(receipt));
        var privacy = host.Services.GetRequiredService<IPrivacyStore>(); var status = await privacy.StatusAsync();
        var erased = await privacy.EraseAsync(new(Guid.NewGuid(), status.Revision, PrivacyData.ErasePhrase), new string('a', 64), DateTimeOffset.UtcNow);
        Assert.Null((await privacy.ExportAsync(DateTimeOffset.UtcNow)).CreationReceipts);
        await privacy.ReopenAsync(new(erased.Revision, PrivacyData.ReopenPhrase), new string('b', 64));
        using var stale = await owner.PostAsJsonAsync("/api/learning", request); Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Empty((await privacy.ExportAsync(DateTimeOffset.UtcNow)).Learning);
    }
}
