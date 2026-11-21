using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Jarvis.Orchestrator.Training;

namespace Jarvis.Tests;

// Synthetic, non-empty preservation baseline for the upcoming training storage migration.
// Keep canonical transitions here so future Up/Down tests can reuse the same history.
internal sealed class TrainingMigrationFixture
{
    internal static readonly DateTimeOffset At = new DateTimeOffset(2026, 3, 28, 23, 45, 0, TimeSpan.FromHours(5.5)).AddTicks(7);
    internal readonly ForgeClock Clock = new() { Now = At };
    internal readonly Guid Session = Guid.NewGuid();
    internal readonly Guid Goal = Guid.NewGuid();
    internal readonly Guid Question = QuestionBank.All[2].Id;
    internal readonly Guid NullOperation = Guid.NewGuid();
    internal readonly ReviewLearningRequest Before = new(Guid.NewGuid(), ReviewRating.Hard, 1.2345678);
    internal readonly ReviewLearningRequest Later = new(Guid.NewGuid(), ReviewRating.Good, 2.3456789);
    internal SubmitTrainingAnswerRequest Submit = null!;
    internal SubmitTrainingAnswerRequest DeletedSubmit = null!;
    internal SelfAssessmentRequest Assessment = null!;
    internal CorrectTrainingAnswerRequest Correction = null!;
    internal RecordingTrainingFixture Purged = null!;

    internal async Task Seed(IRuntimeStore store, ISessionStore sessions)
    {
        Purged = await RecordingTrainingFixture.Seed(store, sessions, questionOnly: true);
        await store.UpdateAsync(s => {
            Purged.Purge(s); Purged.AssertPurged(s);
            // Old snapshots may contain null as well as the current empty purge sentinel.
            s.TrainingOperations[NullOperation] = new("correction", Purged.Attempt, null!);
            var q = QuestionBank.Get(Question);
            s.Learning[Question] = new(Question, q.Domain, q.Prompt, q.Reference, 3, 5, At, At);
            s.Goals[Goal] = new(Goal, 1, "Synthetic unrelated goal", "Preserve cold state", "Daily", false,
                At, At.AddMonths(1), "active", At, At);
            return true;
        });
        await sessions.AddAsync(new(Session, ActivityType.General, "Synthetic migration training", SessionStatus.Active,
            null, null, At, At, []));
        var training = new TrainingService(store, sessions, Clock);
        var learning = new LearningService(store, Clock);
        await learning.ReviewAsync(Question, Before, default);
        var question = QuestionBank.Get(Question);
        Submit = new(Guid.NewGuid(), Session, Question, question.Version, TrainingMode.RapidDrill, "Résumé: await releases the thread — café.");
        var attempt = await training.SubmitAsync(Submit, default);
        Assessment = new(Guid.NewGuid(), attempt.Revision, attempt.TextRevision,
            [new(AssessmentDimension.TechnicalAccuracy, 3, "await releases the thread", "Synthetic quoted evidence")]);
        attempt = await training.AssessAsync(attempt.Id, Assessment, default);
        Clock.Now = At.AddMinutes(1);
        Correction = new(Guid.NewGuid(), attempt.Revision, "Corrected résumé: explain the asynchronous I/O boundary.");
        await training.CorrectAsync(attempt.Id, Correction, default);

        DeletedSubmit = Submit with { RequestId = Guid.NewGuid(), Text = "Synthetic answer that must stay deleted" };
        var deleted = await training.SubmitAsync(DeletedSubmit, default);
        deleted = await training.AssessAsync(deleted.Id, new(Guid.NewGuid(), deleted.Revision, deleted.TextRevision,
            [new(AssessmentDimension.TechnicalAccuracy, 2, "", "Synthetic partial answer")]), default);
        Assert.True(await training.DeleteAsync(deleted.Id, deleted.Revision, default));
        await training.SubmitAsync(Submit with { RequestId = Guid.NewGuid(), Text = "", Skipped = true }, default);
        await learning.ReviewAsync(Question, Later, default);
        await store.ReadAsync(s => {
            Assert.Equal(2, s.TrainingAttempts[Submit.RequestId].Answers.Count);
            Assert.Single(s.TrainingAttempts[Submit.RequestId].Assessments);
            Assert.Null(s.TrainingAttempts[Submit.RequestId].CanonicalAssessmentId);
            Assert.Contains(Before.RequestId, s.TrainingBaselineReviews[Question]);
            Assert.DoesNotContain(Later.RequestId, s.TrainingBaselineReviews[Question]);
            Assert.True(s.TrainingManualSequences[Later.RequestId] > s.TrainingAttempts[Submit.RequestId].Sequence);
            Assert.DoesNotContain(Before.RequestId, s.TrainingManualSequences.Keys);
            Assert.Equal(103, s.NextTrainingSequence);
            Assert.Equal(At.Offset, s.TrainingAttempts[Submit.RequestId].SubmittedAt.Offset);
            Purged.AssertPurged(s, expectedNextSequence: 103);
            return true;
        });
    }

    internal static Task<string> Snapshot(IRuntimeReader reader) => reader.ReadAsync(s => JsonSerializer.Serialize(s));
    internal static void Same(string expected, string actual) => Assert.True(
        JsonNode.DeepEquals(JsonNode.Parse(expected), JsonNode.Parse(actual)), "Canonical runtime values must survive storage restoration and no-op replay.");

    internal async Task Replay(IRuntimeStore store, ISessionStore sessions)
    {
        var before = await Snapshot(store);
        var training = new TrainingService(store, sessions, Clock);
        var current = await training.SubmitAsync(Submit, default);
        Assert.Equal(Correction.Text, current.CurrentAnswer);
        Assert.Equal(current.Revision, (await training.CorrectAsync(current.Id, Correction, default)).Revision);
        Assert.Equal(current.Revision, (await training.AssessAsync(current.Id, Assessment, default)).Revision);
        var learning = new LearningService(store, Clock);
        var original = await learning.ReviewAsync(Question, Before, default);
        Assert.Equal(Before.RequestId, await store.ReadAsync(s => s.Reviews[Before.RequestId].RequestId));
        Assert.Equal(JsonSerializer.Serialize(original), await store.ReadAsync(s => JsonSerializer.Serialize(s.Reviews[Before.RequestId].Result)));
        await learning.ReviewAsync(Question, Later, default);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => training.SubmitAsync(DeletedSubmit, default))).StatusCode);
        foreach (var request in new[] { Purged.Correction, Purged.Correction with { RequestId = NullOperation } }) {
            var error = await Assert.ThrowsAsync<DomainException>(() => training.CorrectAsync(Purged.Attempt, request, default));
            Assert.Equal(409, error.StatusCode); Assert.Equal("training_operation_purged", error.Code);
        }
        Same(before, await Snapshot(store));
    }

    internal async Task EditAfterRestore(IRuntimeStore store, ISessionStore sessions)
    {
        var training = new TrainingService(store, sessions, Clock);
        var current = await store.ReadAsync(s => s.TrainingAttempts[Submit.RequestId]);
        var next = await store.ReadAsync(s => s.NextTrainingSequence);
        Clock.Now = At.AddDays(1);
        var request = new CorrectTrainingAnswerRequest(Guid.NewGuid(), current.Revision, "Post-restore correction with original history retained.");
        var changed = await training.CorrectAsync(current.Id, request, default);
        Assert.Equal(3, changed.Answers.Count); Assert.Single(changed.Assessments);
        Assert.Null(changed.CanonicalAssessmentId);
        Assert.Equal(next, await store.ReadAsync(s => s.NextTrainingSequence));
        var review = new ReviewLearningRequest(Guid.NewGuid(), ReviewRating.Again, 3);
        await new LearningService(store, Clock).ReviewAsync(Question, review, default);
        Assert.Equal(next, await store.ReadAsync(s => s.TrainingManualSequences[review.RequestId]));
        Assert.Equal(next + 1, await store.ReadAsync(s => s.NextTrainingSequence));
        var before = await Snapshot(store);
        await training.CorrectAsync(current.Id, request, default);
        await new LearningService(store, Clock).ReviewAsync(Question, review, default);
        Same(before, await Snapshot(store));
    }
}
