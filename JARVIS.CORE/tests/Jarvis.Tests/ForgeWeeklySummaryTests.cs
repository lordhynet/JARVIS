using System.Net;
using System.Net.Http.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Forge;

namespace Jarvis.Tests;

public sealed class ForgeWeeklySummaryTests
{
    private static readonly DateTimeOffset From = new(2026, 12, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset To = From.AddDays(7);
    private static readonly DateOnly FromDate = new(2026, 12, 1), ToDate = new(2026, 12, 7);

    private static LearningReview Review(LearningDomain domain, DateTimeOffset at)
    {
        var item = new LearningItem(Guid.NewGuid(), domain, "Prompt", "Answer", 1, 5, at.AddDays(3), at.AddDays(-10));
        return new(Guid.NewGuid(), new ReviewEvent(Guid.NewGuid(), item.Id, ReviewRating.Good, TimeSpan.FromSeconds(4), at), item);
    }
    private static TrainingAttempt Training(DateTimeOffset at, bool skipped = false, bool deleted = false) => new(Guid.NewGuid(), Guid.NewGuid(),
        Jarvis.Orchestrator.Training.QuestionBank.All[0].Id, 1, Enum.GetValues<TrainingMode>()[0], 1, 1, skipped, at, [new(1, "Fixture answer", "typed", at)], [], DeletedAt: deleted ? at.AddHours(1) : null);
    private static CoachingAttempt Coaching(DateTimeOffset at, bool deleted = false) =>
        new(Guid.NewGuid(), Guid.NewGuid(), "recruiter", 1, null, null, null, 1, at, [new(1, "Fixture answer", null, at)], deleted ? at.AddHours(1) : null);
    private static PersonalGoal Goal(string? division, string status = GoalStatus.Active) => new(Guid.NewGuid(), 1, "Goal", "Brief",
        "practice-reminder", false, From, To.AddDays(30), status, From.AddDays(-9), From.AddDays(-9), DivisionKey: division);

    [Fact]
    public void Counts_existing_records_by_division_inside_the_window_only()
    {
        var week = ForgeWeeklySummary.Build(From, To, FromDate, ToDate,
            [Review(LearningDomain.SystemDesign, From.AddDays(1)), Review(LearningDomain.French, From.AddDays(2)), Review(LearningDomain.French, From.AddDays(3)),
             Review(LearningDomain.InterviewStory, From.AddDays(4)), Review(LearningDomain.Databases, From.AddDays(-1)), Review(LearningDomain.Databases, To)],
            [Training(From.AddHours(5)), Training(From.AddHours(6), skipped: true), Training(From.AddHours(7), deleted: true), Training(To.AddHours(1))],
            [Coaching(From.AddDays(2)), Coaching(From.AddDays(2), deleted: true)],
            [Goal("forge.body"), Goal("forge.tech"), Goal("forge.wealth", GoalStatus.Paused), Goal(null)]);

        Assert.Equal(4, week.Reviews); Assert.Equal(1, week.TrainingAnswers); Assert.Equal(1, week.CoachingAnswers);
        var tech = week.Divisions.Single(d => d.Key == "forge.tech");
        Assert.Equal(1, tech.Reviews); Assert.Equal(1, tech.TrainingAnswers); Assert.True(tech.HasActiveGoal); Assert.True(tech.Practised);
        Assert.Equal(2, week.Divisions.Single(d => d.Key == "forge.voice").Reviews);
        var career = week.Divisions.Single(d => d.Key == "forge.career");
        Assert.Equal(1, career.Reviews); Assert.Equal(1, career.CoachingAnswers); Assert.False(career.HasActiveGoal);
        var body = week.Divisions.Single(d => d.Key == "forge.body");
        Assert.True(body.HasActiveGoal); Assert.False(body.Practised);
        Assert.DoesNotContain(week.Divisions, d => d.Key == "forge.wealth"); // paused goal, no evidence
        Assert.Equal(4, week.FocusDivisions); Assert.Equal(3, week.PractisedDivisions); Assert.Equal(0.75m, week.PracticeCoverage);
        Assert.Null(week.ActualMinutes); Assert.Null(week.PlannedMinutes);
    }

    [Fact]
    public void Nothing_to_judge_is_unknown_not_zero()
    {
        var week = ForgeWeeklySummary.Build(From, To, FromDate, ToDate, [], [], [], [Goal(null)]);
        Assert.Empty(week.Divisions); Assert.Equal(0, week.FocusDivisions); Assert.Null(week.PracticeCoverage);
        var idle = ForgeWeeklySummary.Build(From, To, FromDate, ToDate, [], [], [], [Goal("forge.social")]);
        Assert.Equal(0m, idle.PracticeCoverage); // known absence of recorded practice, not a zero skill score
    }

    [Fact]
    public async Task Week_endpoint_is_owner_only_forge_gated_and_covers_seven_local_days()
    {
        using (var disabled = new ForgeApiFactory(enabled: false)) {
            using var owner = disabled.Owner();
            using var off = await owner.GetAsync("/api/forge/week");
            Assert.Equal(HttpStatusCode.NotFound, off.StatusCode);
        }
        using var factory = new ForgeApiFactory(); using var client = factory.Owner();
        using (var none = await client.GetAsync("/api/forge/week")) Assert.Equal(HttpStatusCode.NotFound, none.StatusCode);
        await LifeScenarioTests.Post<ForgeProgramme>(client, "/api/forge/programmes", new CreateForgeProgrammeRequest(Guid.NewGuid()));
        var week = (await client.GetFromJsonAsync<ForgeWeek>("/api/forge/week", LifeScenarioTests.Json))!;
        Assert.Equal(new DateOnly(2026, 11, 30), week.To); Assert.Equal(new DateOnly(2026, 11, 24), week.From);
        Assert.Null(week.PracticeCoverage); Assert.Null(week.PlannedMinutes);
    }
}
