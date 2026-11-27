using Jarvis.Contracts;
using Jarvis.Orchestrator.Forge;
using Jarvis.Orchestrator.Runtime;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text.Json;
using Jarvis.Orchestrator.Training;

namespace Jarvis.Tests;

public sealed class ForgeWeekReviewTests
{
    private static readonly DateTimeOffset Now = ForgeTodayReviewTests.Now;
    private static CoachingAttempt Answer(string exercise, DateTimeOffset at) => new(Guid.NewGuid(), Guid.NewGuid(), exercise, 1,
        null, null, null, 1, at, [new(1, "Synthetic private answer", null, at)]);
    private static ForgeWeek Week(params CoachingAttempt[] answers) => ForgeWeeklySummary.Build(Now.AddDays(-6).Date,
        Now.Date.AddDays(1), DateOnly.FromDateTime(Now.AddDays(-6).Date), DateOnly.FromDateTime(Now.Date), [], [], answers, []);

    [Fact]
    public void Known_communication_practice_is_voice_not_interview_credit()
    {
        var week = Week(Answer("point-first", Now));
        Assert.Equal("forge.voice", Assert.Single(week.Divisions).Key);
    }

    [Fact]
    public void Unknown_exercise_does_not_earn_guessed_career_credit()
    {
        var week = Week(Answer("unknown-future-exercise", Now));
        Assert.Equal(1, week.CoachingAnswers);
        Assert.Equal(1, week.UnclassifiedCoachingAnswers); Assert.Null(week.PracticeCoverage);
        Assert.Empty(week.Divisions);
    }

    [Fact]
    public async Task Service_cannot_credit_a_future_answer_later_today()
    {
        using var host = new ForgeApiFactory();
        await host.Services.GetRequiredService<ForgeProgrammeService>().CreateAsync(new(Guid.NewGuid()));
        var runtime = host.Services.GetRequiredService<IRuntimeStore>();
        await runtime.UpdateAsync(s => { var answer = Answer("point-first", Now.AddHours(1)); s.CoachingAttempts[answer.Id] = answer; return true; });
        var week = await host.Services.GetRequiredService<ForgeTodayService>().BuildWeekAsync(Now);
        Assert.NotNull(week); Assert.Equal(0, week.CoachingAnswers);
    }

    [Theory]
    [InlineData("training-version")] [InlineData("coaching-version")] [InlineData("review-domain")]
    public void Unknown_provenance_is_counted_but_cannot_claim_division_coverage(string kind)
    {
        var training = new TrainingAttempt(Guid.NewGuid(), Guid.NewGuid(), QuestionBank.All[0].Id, 99, TrainingMode.DeepDive, 1, 1, false, Now, [new(1, "Fixture answer", "typed", Now)], []);
        var coaching = Answer("point-first", Now) with { ExerciseVersion = 99 };
        var review = Review(Now, (LearningDomain)999);
        var week = ForgeWeeklySummary.Build(Now.AddDays(-6), Now.AddDays(1), DateOnly.FromDateTime(Now.AddDays(-6).Date), DateOnly.FromDateTime(Now.Date),
            kind == "review-domain" ? [review] : [], kind == "training-version" ? [training] : [], kind == "coaching-version" ? [coaching] : [], [], Now);
        Assert.Empty(week.Divisions); Assert.Null(week.PracticeCoverage);
        Assert.Equal(1, week.Reviews + week.TrainingAnswers + week.CoachingAnswers);
        Assert.Equal(1, week.UnclassifiedReviews + week.UnclassifiedTrainingAnswers + week.UnclassifiedCoachingAnswers);
    }

    [Fact]
    public void Corrections_and_assessments_do_not_multiply_one_canonical_attempt()
    {
        var first = Answer("point-first", Now.AddDays(-1));
        var corrected = first with { Revision = 2, Answers = [..first.Answers, new(2, "Corrected synthetic answer", null, Now)] };
        var week = Week(corrected, Answer("recruiter", Now));
        Assert.Equal(2, week.CoachingAnswers);
        Assert.Equal(1, week.Divisions.Single(d => d.Key == "forge.voice").CoachingAnswers);
        Assert.Equal(1, week.Divisions.Single(d => d.Key == "forge.career").CoachingAnswers);
        Assert.Equal(1m, week.PracticeCoverage);
    }

    [Fact]
    public void Skipped_deleted_and_never_answered_attempts_cannot_earn_practice_credit()
    {
        var training = new TrainingAttempt(Guid.NewGuid(), Guid.NewGuid(), QuestionBank.All[0].Id, 1, TrainingMode.DeepDive, 1, 1, false, Now,
            [new(1, "Fixture answer", "typed", Now)], []);
        var coaching = Answer("point-first", Now);
        var week = ForgeWeeklySummary.Build(Now.AddDays(-6), Now.AddDays(1), DateOnly.FromDateTime(Now.AddDays(-6).Date), DateOnly.FromDateTime(Now.Date), [],
            [training with { Answers = [] }, training with { Skipped = true }, training with { DeletedAt = Now }],
            [coaching with { Answers = [] }, coaching with { DeletedAt = Now }], [], Now);
        Assert.Equal(0, week.TrainingAnswers); Assert.Equal(0, week.CoachingAnswers); Assert.Empty(week.Divisions); Assert.Null(week.PracticeCoverage);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Disabled_or_missing_programme_never_reads_personal_runtime(bool enabled)
    {
        var service = new ForgeTodayService(new ForgeTodayReviewTests.AvailabilityFixture(enabled),
            new ForgeTodayReviewTests.Programmes(), new ForbiddenRuntime());
        Assert.Null(await service.BuildWeekAsync(Now));
    }

    [Theory]
    [InlineData("2027-03-28T12:00:00Z", "2027-03-22T00:00:00Z")]
    [InlineData("2027-10-31T12:00:00Z", "2027-10-24T23:00:00Z")]
    public async Task Programme_local_window_uses_exact_ticks_and_captured_now_across_dst(string atText, string startText)
    {
        var at = DateTimeOffset.Parse(atText); var start = DateTimeOffset.Parse(startText); IRuntimeStore store = new InMemoryRuntimeStore();
        await store.UpdateAsync(s => {
            foreach (var time in new[] { start.AddTicks(-1), start, at, at.AddTicks(1) }) { var r = Review(time); s.Reviews[r.RequestId] = r; }
            return true;
        });
        var service = new ForgeTodayService(new ForgeTodayReviewTests.AvailabilityFixture(true), new ForgeTodayReviewTests.Programmes(ForgeTodayReviewTests.Programme()), store);
        var week = await service.BuildWeekAsync(at); Assert.NotNull(week);
        Assert.Equal(2, week.Reviews); Assert.Equal(at, week.AsOf); Assert.Equal(week.To.AddDays(-6), week.From);
    }

    [Fact]
    public async Task Summary_is_owner_authenticated_and_never_returns_private_record_content()
    {
        using var host = new ForgeApiFactory(); using var anonymous = host.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/forge/week")).StatusCode);
        await host.Services.GetRequiredService<ForgeProgrammeService>().CreateAsync(new(Guid.NewGuid()));
        var store = host.Services.GetRequiredService<IRuntimeStore>();
        await store.UpdateAsync(s => {
            var goal = ForgeTodayReviewTests.Goal("PRIVATE_WEEK_SENTINEL", "forge.wealth", 0);
            s.Goals[goal.Id] = goal; var answer = Answer("recruiter", Now); s.CoachingAttempts[answer.Id] = answer; return true;
        });
        var week = await host.Services.GetRequiredService<ForgeTodayService>().BuildWeekAsync(Now);
        var json = JsonSerializer.Serialize(week);
        Assert.DoesNotContain("PRIVATE_WEEK_SENTINEL", json); Assert.DoesNotContain("Synthetic private answer", json);
        Assert.Null(week!.ActualMinutes); Assert.Null(week.PlannedMinutes);
    }

    private static LearningReview Review(DateTimeOffset at, LearningDomain domain = LearningDomain.French)
    {
        var item = new LearningItem(Guid.NewGuid(), domain, "PRIVATE_WEEK_PROMPT", "PRIVATE_WEEK_ANSWER", 1, 5, at, at);
        return new(Guid.NewGuid(), new(Guid.NewGuid(), item.Id, ReviewRating.Good, TimeSpan.FromSeconds(1), at), item);
    }
    private sealed class ForbiddenRuntime : IRuntimeReader
    {
        public Task<T> ReadAsync<T>(Func<RuntimeState, T> read, CancellationToken ct = default) => throw new InvalidOperationException("Unexpected personal runtime read");
    }
}
