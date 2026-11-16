using System.Net;
using System.Net.Http.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Cqrs.Training;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Jarvis.Orchestrator.Training;

namespace Jarvis.Tests;

public sealed class TrainingTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-03-27T12:00:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Fixture
    {
        public readonly Clock Clock = new();
        public readonly InMemoryRuntimeStore Store = new();
        public readonly InMemorySessionStore Sessions = new();
        public readonly Guid Session = Guid.NewGuid();
        public TrainingService Service => new(Store, Sessions, Clock);
        public async Task<TrainingAttempt> Submit(bool skip = false)
        {
            if (await Sessions.GetAsync(Session) is null)
                await Sessions.AddAsync(new(Session, ActivityType.General, "Training", SessionStatus.Active, null, null, Clock.Now, Clock.Now, []));
            return await Service.SubmitAsync(new(Guid.NewGuid(), Session, QuestionBank.All[0].Id, 1, TrainingMode.RapidDrill,
                skip ? "" : "Await releases the thread while I/O is pending.", skip), default);
        }
        public Task<TrainingAttempt> Assess(TrainingAttempt attempt, int? score) => Service.AssessAsync(attempt.Id,
            new(Guid.NewGuid(), attempt.Revision, attempt.TextRevision,
                [new(AssessmentDimension.TechnicalAccuracy, score, score >= 3 ? "Await releases the thread" : "", score is null ? "" : "Compared with reference")]), default);
        public Task<LearningItem> Card() => Store.ReadAsync(s => s.Learning[QuestionBank.All[0].Id]);
    }

    [Theory]
    [InlineData(0, 1)] [InlineData(1, 1)] [InlineData(2, 3)] [InlineData(3, 7)] [InlineData(4, 7)]
    public async Task Content_scores_schedule_calendar_days_across_DST(int score, int days)
    {
        var f = new Fixture(); var attempt = await f.Submit(); await f.Assess(attempt, score);
        var card = await f.Card();
        Assert.Equal((double)score, card.MasteryScore);
        Assert.Equal(TrainingProjection.CalendarDue(attempt.SubmittedAt, days, TrainingProjection.Zone("Europe/London")), card.DueAt);
        Assert.Equal("content-score-v1", card.SchedulingRuleVersion);
    }

    [Fact]
    public async Task Strong_streak_and_correction_replay_later_attempts_from_original_practice_time()
    {
        var f = new Fixture(); var first = await f.Assess(await f.Submit(), 3);
        f.Clock.Now = f.Clock.Now.AddDays(8);
        var second = await f.Assess(await f.Submit(), 4);
        Assert.Equal(14, (await f.Card()).Stability);
        f.Clock.Now = f.Clock.Now.AddDays(30);
        await f.Service.CorrectAsync(first.Id, new(Guid.NewGuid(), first.Revision, "Corrected explanation."), default);
        var card = await f.Card();
        Assert.Equal(1, card.StrongStreak); Assert.Equal(7, card.Stability);
        Assert.Equal(TrainingProjection.CalendarDue(second.SubmittedAt, 7, TrainingProjection.Zone("Europe/London")), card.DueAt);
        Assert.True(await f.Service.DeleteAsync(second.Id, second.Revision, default));
        Assert.Null((await f.Card()).MasteryScore);
    }

    [Fact]
    public async Task Unavailable_metrics_and_skips_do_not_become_failure_scores()
    {
        var f = new Fixture(); var skip = await f.Submit(true);
        Assert.Empty(await f.Store.ReadAsync(s => s.Learning));
        Assert.Null(skip.WordsPerMinute); Assert.Null(skip.AnswerDurationMs);
        var attempt = await f.Assess(await f.Submit(), null);
        Assert.Null(attempt.CanonicalAssessment!.ContentScore); Assert.Null((await f.Card()).MasteryScore);
        Assert.Equal(0, (await f.Card()).Stability);
    }

    [Fact]
    public async Task Communication_only_rating_does_not_imply_content_mastery()
    {
        var f = new Fixture(); var a = await f.Submit();
        var result = await f.Service.AssessAsync(a.Id, new(Guid.NewGuid(), 1, 1,
            [new(AssessmentDimension.CommunicationStructure, 4, "Await releases the thread", "Clear structure")]), default);
        Assert.Null(result.CanonicalAssessment!.ContentScore); Assert.Null((await f.Card()).MasteryScore);
    }

    [Fact]
    public async Task Evidence_must_quote_current_answer_and_strong_scores_need_evidence()
    {
        var f = new Fixture(); var a = await f.Submit();
        foreach (var score in new[] { new DimensionScore(AssessmentDimension.TechnicalAccuracy, 4, "invented", "Reason"),
            new DimensionScore(AssessmentDimension.TechnicalAccuracy, 3, "", "Reason"),
            new DimensionScore(AssessmentDimension.TechnicalAccuracy, 2, "", "") })
        {
            var error = await Assert.ThrowsAsync<DomainException>(() => f.Service.AssessAsync(a.Id, new(Guid.NewGuid(), 1, 1, [score]), default));
            Assert.Equal(400, error.StatusCode);
        }
        Assert.Empty((await f.Store.ReadAsync(s => s.TrainingAttempts[a.Id])).Assessments);
    }

    [Fact]
    public async Task Duplicate_requests_are_idempotent_and_deleted_attempts_cannot_be_resurrected()
    {
        var f = new Fixture(); var a = await f.Submit();
        var request = new SubmitTrainingAnswerRequest(a.Id, f.Session, a.QuestionId, 1, a.Mode, a.CurrentAnswer);
        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => f.Service.SubmitAsync(request, default)));
        Assert.All(responses, x => Assert.Equal(a.Id, x.Id));
        Assert.Single(await f.Store.ReadAsync(s => s.TrainingAttempts));
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => f.Service.SubmitAsync(request with { Text = "different" }, default))).StatusCode);
        await f.Service.DeleteAsync(a.Id, 1, default);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => f.Service.SubmitAsync(request, default))).StatusCode);
        var deleted = await f.Store.ReadAsync(s => s.TrainingAttempts[a.Id]);
        Assert.Empty(deleted.Answers); Assert.Empty(deleted.Assessments);
    }

    [Fact]
    public async Task Concurrent_assessments_have_one_winner_and_replays_return_current_record()
    {
        var f = new Fixture(); var a = await f.Submit();
        var requests = Enumerable.Range(0, 8).Select(_ => new SelfAssessmentRequest(Guid.NewGuid(), 1, 1,
            [new(AssessmentDimension.TechnicalAccuracy, 2, "", "Partial recall")])).ToList();
        var responses = await Task.WhenAll(requests.Select(async r =>
        {
            try { return (await f.Service.AssessAsync(a.Id, r, default), r); }
            catch (DomainException e) { Assert.Equal(409, e.StatusCode); return ((TrainingAttempt?)null, r); }
        }));
        var winner = Assert.Single(responses, x => x.Item1 is not null);
        Assert.Equal(2, winner.Item1!.Revision);
        Assert.Single((await f.Service.AssessAsync(a.Id, winner.r, default)).Assessments);
        var corrected = await f.Service.CorrectAsync(a.Id, new(Guid.NewGuid(), 2, "A new answer"), default);
        Assert.Null(corrected.CanonicalAssessment);
        Assert.Equal(3, (await f.Service.AssessAsync(a.Id, winner.r, default)).Revision);
    }

    [Fact]
    public async Task Existing_manual_reviews_are_not_double_counted_and_later_reviews_survive_corrections()
    {
        var f = new Fixture(); var question = QuestionBank.All[0];
        await f.Store.UpdateAsync(s => { s.Learning[question.Id] = new(question.Id, question.Domain, question.Prompt, question.Reference, 2, 5, f.Clock.Now, f.Clock.Now); return true; });
        var learning = new LearningService(f.Store, f.Clock);
        var baseline = await learning.ReviewAsync(question.Id, new(Guid.NewGuid(), ReviewRating.Good, 2), default);
        var a = await f.Submit(); await f.Assess(a, 4);
        f.Clock.Now = f.Clock.Now.AddDays(1);
        await learning.ReviewAsync(question.Id, new(Guid.NewGuid(), ReviewRating.Good, 2), default);
        await f.Service.DeleteAsync(a.Id, 2, default);
        Assert.Equal(TrainingProjection.Manual(baseline, ReviewRating.Good, f.Clock.Now), await f.Card());
    }

    [Fact]
    public async Task Manual_and_training_events_at_the_same_instant_keep_committed_order()
    {
        var f = new Fixture(); await f.Assess(await f.Submit(), 3);
        await new LearningService(f.Store, f.Clock).ReviewAsync(QuestionBank.All[0].Id, new(Guid.NewGuid(), ReviewRating.Hard, 1), default);
        await f.Assess(await f.Submit(), 3);
        Assert.Equal(1, (await f.Card()).StrongStreak);
        Assert.Equal(7, (await f.Card()).Stability);
    }

    [Fact]
    public async Task Dashboard_counts_actual_practice_dates_and_excludes_deleted_and_skipped_days()
    {
        var f = new Fixture(); await f.Submit(); await f.Submit();
        f.Clock.Now = f.Clock.Now.AddDays(1); await f.Submit(true);
        var result = await new TrainingQueryHandlers(f.Store, f.Clock).HandleAsync(new GetTrainingDashboard("Europe/London"));
        Assert.Equal(2, result.CompletedAttempts); Assert.Equal(1, result.PracticeDays); Assert.Equal(1, result.SkippedAttempts);
        Assert.Empty(result.WeakDomains);
    }

    [Fact]
    public void Calendar_due_preserves_local_clock_and_handles_missing_and_ambiguous_times()
    {
        var zone = TrainingProjection.Zone("Europe/London");
        Assert.Equal(DateTimeOffset.Parse("2026-03-30T11:00Z"), TrainingProjection.CalendarDue(DateTimeOffset.Parse("2026-03-27T12:00Z"), 3, zone));
        Assert.Equal(DateTimeOffset.Parse("2026-03-29T01:30Z"), TrainingProjection.CalendarDue(DateTimeOffset.Parse("2026-03-28T01:30Z"), 1, zone));
        Assert.Equal(DateTimeOffset.Parse("2026-10-25T00:30Z"), TrainingProjection.CalendarDue(DateTimeOffset.Parse("2026-10-24T00:30Z"), 1, zone));
        Assert.Throws<DomainException>(() => TrainingProjection.Zone("Unknown/Zone"));
    }

    [Fact]
    public async Task API_catalog_and_canonical_session_attempt_round_trip()
    {
        using var host = new JarvisApiFactory(); using var client = host.CreateAuthorisedClient();
        var questions = (await client.GetFromJsonAsync<List<TrainingQuestion>>("/api/training/questions", LifeScenarioTests.Json))!;
        Assert.Equal(16, questions.Count); Assert.Equal(8, questions.Select(x => x.Domain).Distinct().Count());
        var session = await LifeScenarioTests.Post<ConversationSession>(client, "/api/sessions", new CreateSessionRequest(ActivityType.General, "Training", null));
        var a = await LifeScenarioTests.Post<TrainingAttempt>(client, "/api/training/attempts", new SubmitTrainingAnswerRequest(Guid.NewGuid(), session.Id, questions[0].Id, 1, TrainingMode.ExplainSimply, "My explanation"));
        var saved = (await client.GetFromJsonAsync<TrainingAttempt>($"/api/training/attempts/{a.Id}", LifeScenarioTests.Json))!;
        Assert.Equal(session.Id, saved.SessionId); Assert.Equal("My explanation", saved.CurrentAnswer);
        var cards = (await client.GetFromJsonAsync<List<LearningItem>>("/api/training/learning", LifeScenarioTests.Json))!;
        Assert.Equal(questions[0].Id, Assert.Single(cards).Id);
        using var wrongVersion = await client.PostAsJsonAsync("/api/training/attempts", new SubmitTrainingAnswerRequest(Guid.NewGuid(), session.Id, questions[0].Id, 99, TrainingMode.RapidDrill, "Text"), LifeScenarioTests.Json);
        Assert.Equal(HttpStatusCode.Conflict, wrongVersion.StatusCode);
        using var delete = await client.DeleteAsync($"/api/training/attempts/{a.Id}?expectedRevision=1"); delete.EnsureSuccessStatusCode();
        using var missing = await client.GetAsync($"/api/training/attempts/{a.Id}"); Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task History_is_bounded_and_stale_corrections_cannot_replace_a_new_answer()
    {
        var f = new Fixture(); var a = await f.Submit();
        for (var i = 0; i < 5; i++) a = await f.Assess(a, 2);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => f.Assess(a, 2))).StatusCode);
        for (var i = 0; i < 4; i++) a = await f.Service.CorrectAsync(a.Id, new(Guid.NewGuid(), a.Revision, $"Corrected {i}"), default);
        Assert.Equal(5, a.Answers.Count);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => f.Service.CorrectAsync(a.Id, new(Guid.NewGuid(), a.Revision, "Sixth"), default))).StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => f.Service.CorrectAsync(a.Id, new(Guid.NewGuid(), 1, "Stale"), default))).StatusCode);
    }

    [Fact]
    public async Task Missing_sessions_and_invalid_dimensions_are_rejected_without_writes()
    {
        var f = new Fixture();
        Assert.Equal(404, (await Assert.ThrowsAsync<DomainException>(() => f.Service.SubmitAsync(new(Guid.NewGuid(), Guid.NewGuid(), QuestionBank.All[0].Id, 1, TrainingMode.RapidDrill, "Answer"), default))).StatusCode);
        Assert.Empty(await f.Store.ReadAsync(s => s.TrainingAttempts));
        var a = await f.Submit();
        foreach (var scores in new IReadOnlyList<DimensionScore>[] {
            [new(AssessmentDimension.TechnicalAccuracy, 5, "", "Invalid")],
            [new(AssessmentDimension.TechnicalAccuracy, 1, "", "Reason"), new(AssessmentDimension.TechnicalAccuracy, 2, "", "Reason")], [] })
            Assert.Equal(400, (await Assert.ThrowsAsync<DomainException>(() => f.Service.AssessAsync(a.Id, new(Guid.NewGuid(), 1, 1, scores), default))).StatusCode);
    }
}
