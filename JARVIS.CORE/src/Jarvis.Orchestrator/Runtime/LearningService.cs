using Jarvis.Contracts;
using Jarvis.Orchestrator.Training;

namespace Jarvis.Orchestrator.Runtime;

public sealed class LearningService(IRuntimeStore store, TimeProvider clock)
{
    public Task<LearningItem> CreateAsync(CreateLearningRequest request, CancellationToken ct)
        => store.CreateLearningItemAsync(request, clock, ct);

    // Validate before storage; generate identity and sample the supplied time only inside its write boundary.
    public static Func<DateTimeOffset, LearningItem> PrepareCreate(CreateLearningRequest request)
    {
        Guard.Require(Enum.IsDefined(request.Domain), 400, "Unknown learning domain.");
        var prompt = Guard.Text(request.Prompt, "Prompt", 2000);
        var answer = Guard.Text(request.Answer, "Answer", 8000);
        var domain = request.Domain;
        return now => new(Guid.NewGuid(), domain, prompt, answer, 0, 5, now, now);
    }

    public Task<LearningItem> ReviewAsync(Guid id, ReviewLearningRequest request, CancellationToken ct)
        => store.ReviewLearningItemAsync(id, request, clock, ct);

    public static void ValidateReview(ReviewLearningRequest request)
    {
        Guard.Require(request.RequestId != Guid.Empty && Enum.IsDefined(request.Rating), 400, "RequestId and valid rating are required.");
        Guard.Require(double.IsFinite(request.ResponseSeconds) && request.ResponseSeconds is >= 0 and <= 3600,
            400, "ResponseSeconds must be between 0 and 3600.");
    }

    // Shared by the HTTP review and atomic voice-review state transition.
    internal static LearningItem Apply(RuntimeState s, Guid id, ReviewLearningRequest request, DateTimeOffset now, string timingSource = "user_reported")
    {
        ValidateReview(request);
        s.Reviews.TryGetValue(request.RequestId, out var previous);
        // Replay never depends on the current card or its continued existence.
        var change = previous is not null
            ? DecideReview(id, request, now, previous, null, null, timingSource)
            : DecideReview(id, request, now, null, s.Learning.GetValueOrDefault(id),
                s.TrainingBaselines.ContainsKey(id) ? s.NextTrainingSequence : null, timingSource);
        if (change.AddedReview is null) return change.Result;
        if (change.ManualSequence is long sequence) {
            s.TrainingManualSequences.Add(request.RequestId, sequence);
            s.NextTrainingSequence = change.NextSequence!.Value;
        }
        s.Learning[id] = change.Result;
        s.Reviews.Add(request.RequestId, change.AddedReview);
        return change.Result;
    }

    // One scheduling/replay decision for both the cohesive voice transaction and scoped storage.
    // A null sequence means no training baseline exists, not a new sequence namespace.
    public static LearningReviewChange DecideReview(Guid id, ReviewLearningRequest request, DateTimeOffset now,
        LearningReview? previous, LearningItem? item, long? sequence, string timingSource = "user_reported")
    {
        if (previous is not null)
        {
            Guard.Require(previous.Event.LearningItemId == id && previous.Event.Rating == request.Rating &&
                previous.Event.ResponseTime.TotalSeconds == request.ResponseSeconds && previous.Event.TimingSource == timingSource,
                409, "RequestId was used for a different review.");
            return new(previous.Result, null, null, null);
        }
        Guard.Require(item is not null, 404, "Learning item not found.");
        var updated = TrainingProjection.Manual(item, request.Rating, now);
        long? next = sequence is long value ? checked(value + 1) : null;
        return new(updated, new(request.RequestId,
            new(Guid.NewGuid(), id, request.Rating, TimeSpan.FromSeconds(request.ResponseSeconds), now, timingSource), updated), sequence, next);
    }
}

public sealed record LearningReviewChange(LearningItem Result, LearningReview? AddedReview, long? ManualSequence, long? NextSequence);
