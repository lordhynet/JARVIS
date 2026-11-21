using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Domains;
using Jarvis.Orchestrator.Forge;

namespace Jarvis.Tests;

public sealed class ForgePlanCandidateTests
{
    private static readonly DateOnly Day = new(2026, 12, 2);
    private static readonly DateTimeOffset DayStart = new(2026, 12, 2, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset DayEnd = DayStart.AddDays(1);

    private static LearningItem Card(LearningDomain domain, DateTimeOffset due) =>
        new(Guid.NewGuid(), domain, "Prompt", "Answer", 0, 5, due, DayStart.AddDays(-10));
    private static PersonalGoal Goal(string? division, string status = GoalStatus.Active, int reviewOffsetHours = -1, int deadlineDays = 10) =>
        new(Guid.NewGuid(), 1, "Goal", "Brief", "practice-reminder", false, DayStart.AddHours(reviewOffsetHours), DayEnd.AddDays(deadlineDays),
            status, DayStart.AddDays(-5), DayStart.AddDays(-5), DivisionKey: division);

    [Fact]
    public void Due_cards_become_one_review_block_per_existing_domain_sized_by_count()
    {
        var cards = Enumerable.Range(0, 3).Select(_ => Card(LearningDomain.French, DayStart.AddHours(8)))
            .Concat(Enumerable.Range(0, 30).Select(_ => Card(LearningDomain.SystemDesign, DayStart.AddHours(-30))))
            .Append(Card(LearningDomain.Databases, DayEnd.AddMinutes(1))) // not due today
            .ToArray();
        var set = ForgePlanCandidates.Build(DayEnd, cards, []);
        Assert.Equal(0, set.Omitted);
        var french = Assert.Single(set.Candidates, c => c.Key == "review.french");
        Assert.Equal(ForgePlanTaskKind.Review, french.Kind); Assert.Equal(6, french.Minutes); Assert.Equal(5, french.MinimumMinutes);
        Assert.Equal(ForgePlanCandidates.MaxReviewBlockMinutes, set.Candidates.Single(c => c.Key == "review.systemdesign").Minutes);
        Assert.DoesNotContain(set.Candidates, c => c.Key == "review.databases");
        Assert.Equal(DayStart.AddHours(-30), set.Candidates.Single(c => c.Key == "review.systemdesign").DueBy);
    }

    [Fact]
    public void Only_active_division_tagged_goals_due_for_review_are_included_with_division_rules()
    {
        var tech = Goal("forge.tech", deadlineDays: 20);
        var career = Goal("forge.career", deadlineDays: 3);
        var relationships = Goal("forge.relationships");
        var set = ForgePlanCandidates.Build(DayEnd, [], [
            tech, career, relationships,
            Goal(null), Goal("forge.tech", GoalStatus.Paused), Goal("forge.tech", GoalStatus.Deleted),
            Goal("forge.tech", reviewOffsetHours: 30), Goal("forge.unknown")]);
        Assert.Equal(3, set.Candidates.Count);
        var c = set.Candidates.Single(x => x.Key == "goal." + career.Id.ToString("N"));
        Assert.Equal(ForgePlanTaskKind.InterviewPractice, c.Kind); Assert.Equal(3, c.Priority); Assert.Equal(career.Deadline, c.DueBy);
        Assert.Equal(ForgePlanTaskKind.Optional, set.Candidates.Single(x => x.Key == "goal." + relationships.Id.ToString("N")).Kind);
        var t = set.Candidates.Single(x => x.Key == "goal." + tech.Id.ToString("N"));
        Assert.Equal(ForgePlanTaskKind.Practice, t.Kind); Assert.Equal(60, t.Minutes); Assert.Equal(15, t.MinimumMinutes); Assert.Equal(20, t.Priority);
        Assert.Equal("goal." + career.Id.ToString("N"), set.Candidates.First().Key); // nearest deadline first among goals
        Assert.All(set.Candidates, x => Assert.True(DomainPolicy.IsValidKey(x.Key)));
        Assert.Equal(16, ForgePlanCandidates.DefaultSessionMinutes.Count);
        Assert.All(ForgeDivisions.All, d => Assert.True(ForgePlanCandidates.DefaultSessionMinutes.ContainsKey(d.Key)));
    }

    [Fact]
    public void The_candidate_limit_keeps_due_reviews_and_reports_what_it_omitted()
    {
        var cards = new[] { Card(LearningDomain.French, DayStart), Card(LearningDomain.Databases, DayStart) };
        var goals = Enumerable.Range(0, 70).Select(i => Goal("forge.tech", deadlineDays: i % 50)).ToArray();
        var set = ForgePlanCandidates.Build(DayEnd, cards, goals);
        Assert.Equal(ForgeDayAllocator.MaximumCandidates, set.Candidates.Count);
        Assert.Equal(72 - ForgeDayAllocator.MaximumCandidates, set.Omitted);
        Assert.Contains(set.Candidates, x => x.Key == "review.french"); Assert.Contains(set.Candidates, x => x.Key == "review.databases");
    }

    [Fact]
    public void Output_is_deterministic_and_refuses_json()
    {
        var cards = new[] { Card(LearningDomain.French, DayStart), Card(LearningDomain.LeetCode, DayStart) };
        var goals = new[] { Goal("forge.voice"), Goal("forge.tech") };
        var a = ForgePlanCandidates.Build(DayEnd, cards, goals);
        var b = ForgePlanCandidates.Build(DayEnd, cards.Reverse(), goals.Reverse());
        Assert.Equal(a.Candidates, b.Candidates);
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Serialize(a));
    }

    [Fact]
    public void Candidates_feed_the_allocator_and_reviews_are_placed_before_goal_work()
    {
        var programme = new ForgeProgramme(Guid.NewGuid(), "owner", 1, 1, new(2026, 12, 1), new(2027, 8, 18), "Europe/London", false,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        ForgeCapacityLocalInterval Block(int from, int to) => new(Day.ToDateTime(new TimeOnly(from, 0)),
            to == 24 ? Day.AddDays(1).ToDateTime(TimeOnly.MinValue) : Day.ToDateTime(new TimeOnly(to, 0)), "Europe/London", TimeSpan.Zero, TimeSpan.Zero);
        var capacity = ForgeCapacityProjection.ForDay(programme, 1, Day, 120, 0, [Block(0, 7), Block(9, 17), Block(23, 24)]);
        var set = ForgePlanCandidates.Build(DayEnd, [Card(LearningDomain.French, DayStart), Card(LearningDomain.French, DayStart)],
            [Goal("forge.tech"), Goal("forge.voice")]);
        var plan = ForgeDayAllocator.Plan(capacity, set.Candidates, DayStart);
        Assert.Equal(ForgePlanStatus.Planned, plan.Status);
        Assert.True(plan.PlannedMinutes <= 120);
        Assert.Equal("review.french", plan.Scheduled.OrderBy(x => x.Start).First().Key);
        Assert.Equal(set.Candidates.Count, plan.Scheduled.Count + plan.Deferred.Count);
    }
}
