using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Recordings;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed class LearningCreationRetentionTests
{
    [Fact]
    public void Creation_receipt_retention_preserves_lineage_until_same_sweep_source_purge_finishes() {
        var state = new RuntimeState(); var at = DateTimeOffset.Parse("2026-12-21T12:00:00Z"); var recording = Guid.NewGuid(); var id = Guid.NewGuid();
        var document = new ImportedDocument(id, "interview", "Synthetic interview", "Tell me about a challenge?", "fixture", 1, at.AddDays(-40),
            [new("interviewer", "Tell me about a challenge?", 0, 10, CaptureTrack: "interviewer"), new(TranscriptSegment.UserSpeaker, "Private captured response", 10, 20)], SourceRecordingId: recording);
        state.ImportedDocuments.Add(id, document); var question = Assert.Single(RecordedInterviewPractice.Questions(state, document, matchStories: false));
        var request = LearningCreationReceiptTests.Request(); var clock = new CreationReceiptClock { Now = at.AddDays(-40) };
        using var epoch = PrivacyContext.Enter(0); var card = LearningService.ApplyCreate(state, request, clock, 0) with { RecordedQuestionId = question.Id };
        state.Learning[card.Id] = card; state.CreationReceipts[request.RequestId!.Value] = state.CreationReceipts[request.RequestId.Value] with { Result = card };
        var selection = PrivacyData.Select(state, [], [], new(), at); Assert.Single(selection.Imports); Assert.Single(selection.CreationReceipts);
        PrivacyData.RetainRuntime(state, new(), at, selection);
        Assert.Empty(state.Learning); Assert.NotNull(state.ImportedDocuments[id].DeletedAt);
        var retired = Assert.Single(state.CreationReceipts.Values); retired.Validate(); Assert.Null(retired.Result); Assert.Null(retired.Fingerprint);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3651)]
    public void Creation_receipt_retention_rejects_invalid_days(int days) =>
        Assert.Equal(400, Assert.Throws<DomainException>(() => PrivacyData.Validate(new(CreationReceiptDays: days))).StatusCode);

    [Fact]
    public void Creation_receipt_retention_policy_is_additive_validated_and_fingerprinted() {
        var legacy = JsonSerializer.Deserialize<RetentionPolicy>("{}"); Assert.NotNull(legacy);
        Assert.Equal(30, legacy.CreationReceiptDays); Assert.False(legacy.Enabled);
        PrivacyData.Validate(new(CreationReceiptDays: 1)); PrivacyData.Validate(new(CreationReceiptDays: 3650));
        Assert.NotEqual(PrivacyData.PolicyHash(legacy), PrivacyData.PolicyHash(legacy with { CreationReceiptDays = 31 }));
        Assert.DoesNotContain("CreationReceipts", JsonSerializer.Serialize(new PrivacyCounts(0, 0, 0, 0, 0, 0, 0)));
    }

    [Fact]
    public async Task Creation_receipt_retention_memory_preserves_cards_boundary_and_legacy_then_retires_original_words() {
        using var host = new ForgeApiFactory(); var store = host.Services.GetRequiredService<IRuntimeStore>();
        var privacy = host.Services.GetRequiredService<IPrivacyStore>(); using var epoch = PrivacyContext.Enter(0);
        var at = DateTimeOffset.Parse("2026-12-21T12:00:00Z"); var clock = new CreationReceiptClock { Now = at.AddDays(-2).AddTicks(-1) };
        var service = new LearningService(store, clock); var request = LearningCreationReceiptTests.Request() with { Prompt = "Expired prompt payload", Answer = "Expired answer payload" };
        var old = await service.CreateAsync(request, default);
        var removedRequest = request with { RequestId = Guid.NewGuid() }; var removed = await service.CreateAsync(removedRequest, default);
        clock.Now = at.AddDays(-2); var edgeRequest = LearningCreationReceiptTests.Request(); var edge = await service.CreateAsync(edgeRequest, default);
        clock.Now = at.AddDays(-1); var recent = await service.CreateAsync(LearningCreationReceiptTests.Request(), default);
        var legacy = await service.CreateAsync(request with { RequestId = null, Prompt = "Legacy card", Answer = "Keep legacy answer" }, default);
        var corrected = old with { Prompt = "Current corrected prompt", Answer = "Current corrected answer" };
        await store.UpdateAsync(s => { s.Learning[old.Id] = corrected; s.Learning.Remove(removed.Id); return true; });
        Assert.Equal(old, await service.CreateAsync(request, default)); Assert.Equal(removed, await service.CreateAsync(removedRequest, default));
        var policy = new RetentionPolicy(CreationReceiptDays: 2); var preview = await privacy.PreviewAsync(policy, at);
        Assert.Equal(2, preview.Eligible.CreationReceipts);
        var apply = new ApplyRetentionRequest(Guid.NewGuid(), at, preview.PolicyFingerprint, PrivacyData.RetentionPhrase);
        Assert.Equal(400, (await Assert.ThrowsAsync<DomainException>(() => privacy.RetainAsync(apply, policy with { CreationReceiptDays = 3 }, at))).StatusCode);
        var job = await privacy.RetainAsync(apply, policy, at); Assert.Equal(preview.Eligible, job.Removed);
        Assert.Equal(job, await privacy.RetainAsync(apply, policy, at.AddMinutes(1)));
        Assert.Equal(corrected, await store.ReadLearningItemAsync(old.Id)); Assert.Null(await store.ReadLearningItemAsync(removed.Id));
        Assert.Equal(edge, await service.CreateAsync(edgeRequest, default)); Assert.Equal(recent, await store.ReadLearningItemAsync(recent.Id));
        Assert.Equal(legacy, await store.ReadLearningItemAsync(legacy.Id));
        var error = await Assert.ThrowsAsync<DomainException>(() => service.CreateAsync(request with { Answer = "Changed retry" }, default));
        Assert.Equal(410, error.StatusCode); Assert.Equal("learning_creation_retired", error.Code);
        var export = await privacy.ExportAsync(at); var tombstone = Assert.Single(export.CreationReceipts!, x => x.RequestId == request.RequestId);
        Assert.Null(tombstone.Result); Assert.Null(tombstone.CardId); Assert.Null(tombstone.CreatedAt); Assert.Equal(at, tombstone.RetiredAt);
        var json = await store.ReadAsync(s => JsonSerializer.Serialize(s.CreationReceipts[request.RequestId!.Value]));
        Assert.DoesNotContain("Expired", JsonSerializer.Serialize(export)); Assert.DoesNotContain("Fingerprint", json);
        using var parsed = JsonDocument.Parse(json); Assert.Equal(4, parsed.RootElement.EnumerateObject().Count());
        Assert.Equal(0, (await privacy.PreviewAsync(policy, at)).Eligible.CreationReceipts);
        await privacy.RetainAsync(apply with { RequestId = Guid.NewGuid() }, policy, at);
        Assert.Equal(at, await store.ReadAsync(s => s.CreationReceipts[request.RequestId!.Value].RetiredAt));
        var status = await privacy.StatusAsync(); await privacy.EraseAsync(new(Guid.NewGuid(), status.Revision, PrivacyData.ErasePhrase), new string('a', 64), at);
        using var erasedEpoch = PrivacyContext.Enter(1);
        Assert.Null((await privacy.ExportAsync(at)).CreationReceipts);
        Assert.Equal(4, Assert.Single(await privacy.JobsAsync()).Removed.CreationReceipts);
    }
}
