using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Tests;

public sealed class LearningReviewReplayPrecisionTests
{
    [Theory]
    [InlineData(1.2345678)]
    [InlineData(2.3456789)]
    [InlineData(1.23456789)]
    [InlineData(0.00000009)]
    [InlineData(0)]
    [InlineData(3600)]
    public async Task Review_replay_uses_stored_duration_precision_and_rejects_changed_ticks(double seconds)
    {
        IRuntimeStore store = new InMemoryRuntimeStore();
        var service = new LearningService(store, new ForgeClock { Now = TrainingMigrationFixture.At });
        var card = await service.CreateAsync(new(LearningDomain.French, "Synthetic prompt", "Synthetic answer"), default);
        var request = new ReviewLearningRequest(Guid.NewGuid(), ReviewRating.Hard, seconds);
        var original = await service.ReviewAsync(card.Id, request, default);
        Assert.Equal(TimeSpan.FromSeconds(seconds), await store.ReadAsync(s => s.Reviews[request.RequestId].Event.ResponseTime));
        await store.UpdateAsync(s => s.Learning.Remove(card.Id));
        var before = await store.ReadAsync(s => JsonSerializer.Serialize(s));
        Assert.Equal(original, await service.ReviewAsync(card.Id, request, default));
        var changed = request with { ResponseSeconds = seconds < 3600 ? seconds + 0.000001 : seconds - 0.000001 };
        var error = await Assert.ThrowsAsync<DomainException>(() => service.ReviewAsync(card.Id, changed, default));
        Assert.Equal(409, error.StatusCode);
        Assert.Equal(before, await store.ReadAsync(s => JsonSerializer.Serialize(s)));
    }
}
