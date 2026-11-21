using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Recordings;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Jarvis.Orchestrator.Training;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Jarvis.Tests;

internal sealed record RecordingTrainingFixture(Guid Recording, Guid Document, Guid Question, Guid Attempt, Guid Operation,
    Guid Review, Guid Receipt, Guid UnrelatedQuestion, Guid UnrelatedAttempt, CorrectTrainingAnswerRequest Correction)
{
    internal const string Marker = "Synthetic erased recording payload";
    internal static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-20T22:00:00Z");
    internal static async Task<RecordingTrainingFixture> Seed(IRuntimeStore store, ISessionStore sessions, bool questionOnly) {
        var recording = Guid.NewGuid(); var document = Guid.NewGuid(); var question = QuestionBank.All[0].Id; var attempt = Guid.NewGuid();
        var session = Guid.NewGuid(); var review = Guid.NewGuid(); var receipt = Guid.NewGuid(); var unrelated = QuestionBank.All[1].Id; var unrelatedAttempt = Guid.NewGuid();
        await sessions.AddAsync(new(session, ActivityType.General, "Synthetic privacy fixture", SessionStatus.Active, null, null, At, At, []));
        await store.UpdateAsync(s => {
            var doc = new ImportedDocument(document, "interview", Marker, Marker, "fixture-hash", 1, At,
                [new("interviewer", "Why did you choose a fallback?", 0, 10), new(TranscriptSegment.UserSpeaker, Marker, 10, 20)], SourceRecordingId: recording);
            s.ImportedDocuments[document] = doc; var recordedQuestion = Assert.Single(RecordedInterviewPractice.Questions(s, doc, matchStories: false));
            var original = new LearningItem(question, QuestionBank.All[0].Domain, Marker + " prompt", Marker + " reference", 2, 5, At, At);
            var baseline = original with { SourceRecordingId = questionOnly ? null : recording, RecordedQuestionId = recordedQuestion.Id };
            s.TrainingBaselines[question] = baseline; s.Learning[question] = baseline;
            // Restored/legacy lineage can remain only on the baseline after the current card is gone.
            s.Reviews[review] = new(review, new(Guid.NewGuid(), question, ReviewRating.Good, TimeSpan.FromSeconds(2), At), original);
            s.TrainingBaselineReviews[question] = [review]; s.TrainingManualSequences[review] = 77; s.NextTrainingSequence = 99;
            s.CreationReceipts[receipt] = new(receipt, 0, LearningCreationReceipt.Created, 1,
                LearningCreationReceipt.FingerprintFor(new(original.Domain, original.Prompt, original.Answer, receipt)), original, null);
            var assessment = new TrainingAssessment(Guid.NewGuid(), 1, 1, "self", "fixture-v1",
                [new(AssessmentDimension.TechnicalAccuracy, 3, Marker, Marker)], 3, "Europe/London", At);
            s.TrainingAttempts[attempt] = new(attempt, session, question, 1, TrainingMode.RapidDrill, 1, 2, false, At,
                [new(1, Marker, "text_only", At)], [assessment], assessment.Id);
            var keep = new LearningItem(unrelated, QuestionBank.All[1].Domain, "Unrelated prompt", "Unrelated answer", 2, 5, At, At);
            s.Learning[unrelated] = keep; s.TrainingBaselines[unrelated] = keep;
            s.TrainingAttempts[unrelatedAttempt] = new(unrelatedAttempt, session, unrelated, 1, TrainingMode.RapidDrill, 2, 1, false, At,
                [new(1, "Keep unrelated answer", "text_only", At)], []);
            var cache = Guid.NewGuid(); s.ProviderResults[cache] = new(cache, attempt, "fixture", Marker, At);
            return true;
        });
        var correction = new CorrectTrainingAnswerRequest(Guid.NewGuid(), 2, Marker + " corrected");
        await new TrainingService(store, sessions, new ForgeClock { Now = At }).CorrectAsync(attempt, correction, default);
        await store.UpdateAsync(s => s.Learning.Remove(question));
        return new(recording, document, question, attempt, correction.RequestId, review, receipt, unrelated, unrelatedAttempt, correction);
    }
    internal void Purge(RuntimeState s) => ImportService.Purge(s, s.ImportedDocuments[Document], At);
    internal void AssertPurged(RuntimeState s, long expectedNextSequence = 99) {
        Assert.False(s.Learning.ContainsKey(Question)); Assert.False(s.TrainingBaselines.ContainsKey(Question));
        Assert.False(s.TrainingBaselineReviews.ContainsKey(Question)); Assert.False(s.Reviews.ContainsKey(Review)); Assert.False(s.TrainingManualSequences.ContainsKey(Review));
        var retired = s.TrainingAttempts[Attempt]; Assert.NotNull(retired.DeletedAt); Assert.Empty(retired.Answers); Assert.Empty(retired.Assessments); Assert.Null(retired.CanonicalAssessmentId);
        Assert.Equal(4, retired.Revision); Assert.Equal("", s.TrainingOperations[Operation].Fingerprint); Assert.Contains(Attempt, s.RetiredReasoningScopes);
        Assert.DoesNotContain(s.ProviderResults.Values, x => x.ScopeId == Attempt);
        Assert.Equal(LearningCreationReceipt.Retired, s.CreationReceipts[Receipt].Status); Assert.Null(s.CreationReceipts[Receipt].Result);
        Assert.Equal(expectedNextSequence, s.NextTrainingSequence); Assert.Equal("Unrelated answer", s.Learning[UnrelatedQuestion].Answer);
        Assert.Null(s.TrainingAttempts[UnrelatedAttempt].DeletedAt); Assert.Equal("Keep unrelated answer", s.TrainingAttempts[UnrelatedAttempt].CurrentAnswer);
        Assert.DoesNotContain(Marker, JsonSerializer.Serialize(s));
        Assert.Throws<KeyNotFoundException>(() => TrainingProjection.Rebuild(s, Question)); Assert.False(s.Learning.ContainsKey(Question));
    }
}

public sealed class RecordingTrainingPrivacyTests
{
    [Fact]
    public async Task Recording_privacy_retention_does_not_apply_training_tombstone_twice_in_one_sweep() {
        IRuntimeStore store = new InMemoryRuntimeStore(); var sessions = new InMemorySessionStore(); using var epoch = PrivacyContext.Enter(0);
        var fixture = await RecordingTrainingFixture.Seed(store, sessions, questionOnly: true); var at = RecordingTrainingFixture.At.AddDays(2);
        await store.UpdateAsync(s => {
            var keep = s.TrainingAttempts[fixture.UnrelatedAttempt];
            s.TrainingAttempts[keep.Id] = keep with { Answers = [keep.Answers[0] with { At = at }] };
            var policy = new RetentionPolicy(AnswerDays: 1, ArtifactDays: 1); var selection = PrivacyData.Select(s, [], [], policy, at);
            Assert.Contains(fixture.Attempt, selection.Training); Assert.DoesNotContain(fixture.UnrelatedAttempt, selection.Training);
            PrivacyData.RetainRuntime(s, policy, at, selection); return true;
        });
        await store.ReadAsync(s => { fixture.AssertPurged(s); return true; });
    }

    [Fact]
    public void Ordinary_card_removal_keeps_baseline_authority_for_canonical_training_rebuild() {
        var fixture = LearningMutationFixture.Make(32, 6); var s = fixture.State; var expected = s.Learning[fixture.Question];
        s.Learning.Remove(fixture.Question); TrainingProjection.Rebuild(s, fixture.Question);
        Assert.Equal(expected, s.Learning[fixture.Question]); Assert.Null(s.Learning[fixture.Question].SourceRecordingId);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recording_privacy_purge_removes_orphan_training_baseline_and_prevents_card_recreation(bool questionOnly) {
        IRuntimeStore store = new InMemoryRuntimeStore(); var sessions = new InMemorySessionStore(); using var epoch = PrivacyContext.Enter(0);
        var fixture = await RecordingTrainingFixture.Seed(store, sessions, questionOnly);
        await store.UpdateAsync(s => { fixture.Purge(s); return true; });
        await store.ReadAsync(s => { fixture.AssertPurged(s); return true; });
        var service = new TrainingService(store, sessions, new ForgeClock { Now = RecordingTrainingFixture.At });
        var error = await Assert.ThrowsAsync<DomainException>(() => service.CorrectAsync(fixture.Attempt, fixture.Correction, default));
        Assert.Equal(409, error.StatusCode); Assert.Equal("training_operation_purged", error.Code);
        await store.UpdateAsync(s => { fixture.Purge(s); return true; });
        await store.ReadAsync(s => { fixture.AssertPurged(s); return true; });
    }
}

public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task Recording_training_privacy_is_atomic_and_durable_without_recreating_a_deleted_source_card() {
        await using var database = await TestDatabase.CreateAsync(); using var root = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var host = root.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        var store = host.Services.GetRequiredService<IRuntimeStore>(); using var epoch = PrivacyContext.Enter(0);
        var fixture = await RecordingTrainingFixture.Seed(store, host.Services.GetRequiredService<ISessionStore>(), questionOnly: true);
        var transaction = host.Services.GetRequiredService<ICoreTransaction>();
        await Assert.ThrowsAsync<IOException>(() => transaction.ExecuteAsync<bool>(w => { fixture.Purge(w.Runtime); throw new IOException("Synthetic privacy rollback"); }));
        Assert.True(await store.ReadAsync(s => s.TrainingBaselines.ContainsKey(fixture.Question)));
        Assert.Null(await store.ReadAsync(s => s.TrainingAttempts[fixture.Attempt].DeletedAt));
        await transaction.ExecuteAsync(w => { fixture.Purge(w.Runtime); return Task.FromResult(true); });
        using var restartedRoot = new PostgresApiFactory(database.ConnectionString, manualAgents: true);
        using var restarted = restartedRoot.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
        await restarted.Services.GetRequiredService<IRuntimeStore>().ReadAsync(s => { fixture.AssertPurged(s); return true; });
        var export = await restarted.Services.GetRequiredService<IPrivacyStore>().ExportAsync(RecordingTrainingFixture.At);
        Assert.DoesNotContain(RecordingTrainingFixture.Marker, JsonSerializer.Serialize(export));
        var error = await Assert.ThrowsAsync<DomainException>(() => restarted.Services.GetRequiredService<TrainingService>().CorrectAsync(fixture.Attempt, fixture.Correction, default));
        Assert.Equal(409, error.StatusCode); Assert.Equal("training_operation_purged", error.Code);
    }
}
