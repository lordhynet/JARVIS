using Jarvis.Contracts;

namespace Jarvis.Orchestrator.Runtime;

public sealed class LearningService(IRuntimeStore store, TimeProvider clock)
{
    public Task<LearningItem> CreateAsync(CreateLearningRequest request, CancellationToken ct)
    {
        Guard.Require(Enum.IsDefined(request.Domain), 400, "Unknown learning domain.");
        var prompt = Guard.Text(request.Prompt, "Prompt", 2000);
        var answer = Guard.Text(request.Answer, "Answer", 8000);
        return store.UpdateAsync(s =>
        {
            var now = clock.GetUtcNow();
            var item = new LearningItem(Guid.NewGuid(), request.Domain, prompt, answer, 0, 5, now, now);
            s.Learning.Add(item.Id, item);
            return item;
        }, ct);
    }

    public Task<LearningItem> ReviewAsync(Guid id, ReviewLearningRequest request, CancellationToken ct)
    {
        Guard.Require(request.RequestId != Guid.Empty && Enum.IsDefined(request.Rating), 400, "RequestId and valid rating are required.");
        Guard.Require(double.IsFinite(request.ResponseSeconds) && request.ResponseSeconds is >= 0 and <= 3600,
            400, "ResponseSeconds must be between 0 and 3600.");
        return store.UpdateAsync(s =>
        {
            if (s.Reviews.TryGetValue(request.RequestId, out var previous))
            {
                Guard.Require(previous.Event.LearningItemId == id && previous.Event.Rating == request.Rating &&
                    previous.Event.ResponseTime.TotalSeconds == request.ResponseSeconds, 409, "RequestId was used for a different review.");
                return previous.Result;
            }
            Guard.Require(s.Learning.TryGetValue(id, out var item), 404, "Learning item not found.");
            var days = request.Rating switch
            {
                ReviewRating.Again => 10d / 1440,
                ReviewRating.Hard => Math.Max(1, item!.Stability * 1.2),
                ReviewRating.Good => Math.Max(1, item!.Stability * 2.5),
                _ => Math.Max(4, item!.Stability * 3.5)
            };
            days = Math.Min(days, 3650);
            var now = clock.GetUtcNow();
            var updated = item! with
            {
                Stability = days,
                Difficulty = Math.Clamp(item.Difficulty + (request.Rating == ReviewRating.Again ? 1 : request.Rating == ReviewRating.Easy ? -0.5 : 0), 1, 10),
                DueAt = now.AddDays(days)
            };
            s.Learning[id] = updated;
            s.Reviews.Add(request.RequestId, new(request.RequestId,
                new(Guid.NewGuid(), id, request.Rating, TimeSpan.FromSeconds(request.ResponseSeconds), now), updated));
            return updated;
        }, ct);
    }

}
