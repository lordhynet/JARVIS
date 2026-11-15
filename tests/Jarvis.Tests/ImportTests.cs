using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Tests;

public sealed class ImportTests
{
    [Fact]
    public async Task Cv_claims_require_exact_spans_and_source_deletion_purges_derivatives()
    {
        var store = new InMemoryRuntimeStore(); var service = new ImportService(store, TimeProvider.System);
        var request = new ImportDocumentRequest(Guid.NewGuid(), "cv", "CV", "I built a retry-safe payment workflow.", true);
        await Assert.ThrowsAsync<DomainException>(() => service.ImportAsync(request with { ConfirmStorageConsent = false }));
        var document = await service.ImportAsync(request); Assert.Equal(System.Text.Json.JsonSerializer.Serialize(document), System.Text.Json.JsonSerializer.Serialize(await service.ImportAsync(request)));
        var storyId = Guid.NewGuid(); await store.UpdateAsync(s => { s.Stories[storyId] = new(storyId, new("Payments", "Bank", "Transfers"), 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow); return true; });
        var link = new AttachSourceClaimRequest(Guid.NewGuid(), storyId, 1, 1, 0, request.Text.Length);
        await Assert.ThrowsAsync<DomainException>(() => service.AttachClaimAsync(document.Id, link with { Length = 10000 }));
        var claim = await service.AttachClaimAsync(document.Id, link); Assert.Equal(request.Text, claim.Quote);
        Assert.Equal(claim, await service.AttachClaimAsync(document.Id, link));
        Assert.Equal(2, await store.ReadAsync(s => s.Stories[storyId].Version));
        await service.DeleteAsync(document.Id, 1);
        Assert.Empty(await store.ReadAsync(s => s.Stories[storyId].Content.CvClaims!));
        Assert.Empty(await store.ReadAsync(s => s.SourceClaims));
        Assert.Empty(await store.ReadAsync(s => s.ImportedDocuments[document.Id].Text));
        await Assert.ThrowsAsync<DomainException>(() => service.ImportAsync(request));
    }
    [Fact]
    public async Task Interview_import_requires_authorisation_and_timing_matches_transcript()
    {
        var store = new InMemoryRuntimeStore(); var service = new ImportService(store, TimeProvider.System);
        var request = new ImportDocumentRequest(Guid.NewGuid(), "interview", "Debrief", "Why retries?\nFor transient failures.", true);
        await Assert.ThrowsAsync<DomainException>(() => service.ImportAsync(request));
        var document = await service.ImportAsync(request with { ConfirmRecordingAuthorised = true });
        var review = ImportService.Review(document); Assert.Single(review.Questions); Assert.Null(review.TimedUserWordsPerMinute);
        var timed = request with { RequestId = Guid.NewGuid(), ConfirmRecordingAuthorised = true, Segments = [new("interviewer", "Why retries?", 0, 2), new("user", "For transient failures.", 3, 6)] };
        Assert.Equal(60d, ImportService.Review(await service.ImportAsync(timed)).TimedUserWordsPerMinute);
        await Assert.ThrowsAsync<DomainException>(() => service.ImportAsync(timed with { RequestId = Guid.NewGuid(), Segments = [new("user", "Invented", 0, double.NaN)] }));
        await Assert.ThrowsAsync<DomainException>(() => service.ImportAsync(timed with { RequestId = Guid.NewGuid(), Segments = [new("user", "Different text", 0, 1)] }));
    }
}
