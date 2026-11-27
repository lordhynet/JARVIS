using System.Security.Cryptography;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Jarvis.Orchestrator.Training;

namespace Jarvis.Tests;

public sealed class TrainingCorrectionDecisionTests
{
    private static readonly DateTimeOffset At = TrainingMigrationFixture.At;
    private static TrainingAttempt Attempt(Guid id) => new(id, Guid.NewGuid(), QuestionBank.All[0].Id, 1,
        TrainingMode.RapidDrill, 19, 2, false, At, [new(1, "Original answer", "text_only", At)],
        [new(id, 1, 1, "self", "self-rubric-v1", [], 3, "Europe/London", At)], id);
    private static LearningItem Baseline(Guid id) => new(id, LearningDomain.CSharpDotNet, "Synthetic question", "Synthetic reference", 3, 5, At, At);

    [Fact]
    public void Normalization_preserves_the_existing_receipt_hash_contract_and_correction_history()
    {
        var id = Guid.NewGuid(); var current = Attempt(id); var request = new CorrectTrainingAnswerRequest(Guid.NewGuid(), 2, "  Résumé — corrected café.  ");
        var normalized = TrainingCorrection.Normalize(request); var text = request.Text.Trim();
        Assert.Equal(Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { request.ExpectedRevision, text }))), normalized.Fingerprint);
        var change = TrainingCorrection.Decide(id, normalized, null, current, At.AddMinutes(1));
        Assert.Equal(3, change.Result.Revision); Assert.Null(change.Result.CanonicalAssessmentId); Assert.Single(change.Result.Assessments);
        Assert.Equal(current.Sequence, change.Result.Sequence); Assert.Equal(current.SubmittedAt.Offset, change.Result.SubmittedAt.Offset);
        Assert.Equal(text, change.Result.CurrentAnswer); Assert.Equal("user_corrected", change.Result.Answers[^1].Source);
        Assert.Equal(normalized.Fingerprint, change.Operation!.Fingerprint); Assert.Single(current.Answers);
    }

    [Theory]
    [InlineData("purged-null", 409)] [InlineData("purged-empty", 409)] [InlineData("wrong-kind", 409)]
    [InlineData("wrong-target", 409)] [InlineData("wrong-input", 409)] [InlineData("deleted-replay", 409)]
    [InlineData("orphan-replay", 409)] [InlineData("stale", 409)] [InlineData("skipped", 409)]
    [InlineData("full", 409)] [InlineData("missing", 404)] [InlineData("deleted-new", 404)]
    public void Existing_refusal_rules_are_shared_and_fail_closed(string scenario, int expected)
    {
        var id = Guid.NewGuid(); TrainingAttempt? current = Attempt(id);
        var normalized = TrainingCorrection.Normalize(new(Guid.NewGuid(), 2, "Corrected")); TrainingOperation? prior = null;
        if (scenario.Contains("replay") || scenario.StartsWith("purged", StringComparison.Ordinal) || scenario.StartsWith("wrong", StringComparison.Ordinal))
            prior = new("correction", id, normalized.Fingerprint);
        switch (scenario) {
            case "purged-null": prior = prior! with { Fingerprint = null! }; break;
            case "purged-empty": prior = prior! with { Fingerprint = "" }; break;
            case "wrong-kind": prior = prior! with { Kind = "submit" }; break;
            case "wrong-target": prior = prior! with { AttemptId = Guid.NewGuid() }; break;
            case "wrong-input": prior = prior! with { Fingerprint = "different" }; break;
            case "deleted-replay": case "deleted-new": current = current with { DeletedAt = At }; break;
            case "orphan-replay": case "missing": current = null; break;
            case "stale": current = current with { Revision = 3 }; break;
            case "skipped": current = current with { Skipped = true }; break;
            case "full": current = current with { Answers = Enumerable.Range(1, 5).Select(i => new TrainingAnswerVersion(i, "Full history", "text_only", At)).ToArray() }; break;
        }
        var error = Assert.Throws<DomainException>(() => TrainingCorrection.Decide(id, normalized, prior, current, At)); Assert.Equal(expected, error.StatusCode);
        if (scenario.StartsWith("purged", StringComparison.Ordinal)) Assert.Equal("training_operation_purged", error.Code);
    }

    [Fact]
    public void Matching_legacy_replay_returns_current_state_without_new_operation_or_revision_check()
    {
        var id = Guid.NewGuid(); var normalized = TrainingCorrection.Normalize(new(Guid.NewGuid(), 2, "Corrected"));
        var current = Attempt(id) with { Revision = 5 };
        var replay = TrainingCorrection.Decide(id, normalized, new("correction", id, normalized.Fingerprint), current, At);
        Assert.Same(current, replay.Result); Assert.Null(replay.Operation);
    }

    [Fact]
    public void Missing_baseline_refuses_before_any_cohesive_mutation_and_missing_card_rebuilds_from_live_baseline()
    {
        var id = Guid.NewGuid(); var state = new RuntimeState(); state.TrainingAttempts[id] = Attempt(id);
        var request = TrainingCorrection.Normalize(new(Guid.NewGuid(), 2, "Corrected"));
        Assert.Equal("training_baseline_missing", Assert.Throws<DomainException>(() => TrainingCorrection.Apply(state, id, request, new ForgeClock { Now = At })).Code);
        Assert.Equal(2, state.TrainingAttempts[id].Revision); Assert.Empty(state.TrainingOperations); Assert.Empty(state.Learning);
        var question = state.TrainingAttempts[id].QuestionId; state.TrainingBaselines[question] = Baseline(question);
        TrainingCorrection.Apply(state, id, request, new ForgeClock { Now = At }); Assert.True(state.Learning.ContainsKey(question));
        Assert.Equal(1, state.NextTrainingSequence);
    }

    [Fact]
    public void Full_and_event_only_projection_share_fractional_time_and_legacy_tie_order()
    {
        var q = Guid.NewGuid(); var baseline = Baseline(q);
        var first = Guid.Parse("00000000-0000-0000-0000-000000000001"); var second = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var events = new[] { new TrainingReviewInput(first, new(Guid.NewGuid(), q, ReviewRating.Again, TimeSpan.FromTicks(123456789), At), 0),
            new TrainingReviewInput(second, new(Guid.NewGuid(), q, ReviewRating.Good, TimeSpan.FromTicks(1), At.ToOffset(TimeSpan.FromHours(-4))), 0) };
        var full = events.Reverse().Select(r => new LearningReview(r.RequestId, r.Event, baseline)).ToArray();
        var expected = TrainingProjection.Project(q, baseline, new HashSet<Guid>(), full, new Dictionary<Guid, long>(), []);
        var actual = TrainingProjection.ProjectEvents(q, baseline, new HashSet<Guid>(), events, []);
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
        Assert.Equal(At.AddDays(1), actual.DueAt); Assert.Equal(1d, actual.Stability);
        Assert.Throws<InvalidDataException>(() => TrainingProjection.ProjectEvents(q, baseline, new HashSet<Guid>(),
            [events[0] with { Event = events[0].Event with { Rating = (ReviewRating)99 } }], []));
    }

    [Fact]
    public async Task Service_uses_the_correction_port_instead_of_a_broad_update_callback()
    {
        var store = new CorrectionPortSpy(Attempt(Guid.NewGuid()));
        var request = new CorrectTrainingAnswerRequest(Guid.NewGuid(), 2, "Corrected");
        var result = await new TrainingService(store, new InMemorySessionStore(), new ForgeClock { Now = At }).CorrectAsync(store.Result.Id, request, default);
        Assert.Same(store.Result, result); Assert.Equal(1, store.Calls);
    }
    private sealed class CorrectionPortSpy(TrainingAttempt result) : IRuntimeStore
    {
        public TrainingAttempt Result { get; } = result; public int Calls { get; private set; }
        public Task<TrainingAttempt> CorrectTrainingAnswerAsync(Guid id, CorrectTrainingAnswerRequest request, TimeProvider clock, CancellationToken ct = default) { Calls++; return Task.FromResult(Result); }
        public Task<T> ReadAsync<T>(Func<RuntimeState, T> read, CancellationToken ct = default) => throw new InvalidOperationException("Unexpected broad read");
        public Task<T> UpdateAsync<T>(Func<RuntimeState, T> update, CancellationToken ct = default) => throw new InvalidOperationException("Unexpected broad write");
    }
}
