using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Training;

namespace Jarvis.Tests;

public sealed class TrainingProjectionInputTests
{
    [Fact]
    public void Pure_projection_accepts_question_scoped_inputs_and_preserves_all_original_state() {
        var fixture = LearningMutationFixture.Make(32, 6); var s = fixture.State; var question = fixture.Question;
        var before = JsonSerializer.Serialize(s);
        var scoped = TrainingProjection.Project(question, s.TrainingBaselines[question], s.TrainingBaselineReviews[question],
            s.Reviews.Values.Where(x => x.Event.LearningItemId == question).ToArray(), s.TrainingManualSequences,
            s.TrainingAttempts.Values.Where(x => x.QuestionId == question).ToArray());
        var all = TrainingProjection.Project(question, s.TrainingBaselines[question], s.TrainingBaselineReviews[question],
            s.Reviews.Values, s.TrainingManualSequences, s.TrainingAttempts.Values);
        Assert.Equal(scoped, all); Assert.Equal(s.Learning[question], scoped);
        Assert.Equal(4d, scoped.MasteryScore); Assert.Equal(2, scoped.StrongStreak); Assert.Equal(7, scoped.Difficulty);
        Assert.Equal(35, scoped.Stability); Assert.Equal(LearningMutationFixture.At.AddDays(36), scoped.DueAt);
        Assert.Equal(before, JsonSerializer.Serialize(s));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(20)]
    public void Pure_projection_orders_manual_review_and_assessment_at_same_time_by_existing_sequence(long reviewSequence) {
        var at = LearningMutationFixture.At; var id = Guid.NewGuid(); var baseline = new LearningItem(id, LearningDomain.CSharpDotNet, "Synthetic prompt", "Synthetic answer", 0, 5, at, at);
        var assessment = new TrainingAssessment(Guid.NewGuid(), 1, 1, "self", "fixture-v1", [], 3, "Europe/London", at);
        var attempt = new TrainingAttempt(Guid.NewGuid(), Guid.NewGuid(), id, 1, TrainingMode.RapidDrill, 10, 2, false, at,
            [new(1, "Synthetic assessed answer", "text_only", at)], [assessment], assessment.Id);
        var request = Guid.NewGuid(); var review = new LearningReview(request, new(Guid.NewGuid(), id, ReviewRating.Again, TimeSpan.FromSeconds(2), at), baseline);
        var result = TrainingProjection.Project(id, baseline, new HashSet<Guid>(), [review], new Dictionary<Guid, long> { [request] = reviewSequence }, [attempt]);
        Assert.Equal(6, result.Difficulty); Assert.Equal(3d, result.MasteryScore);
        if (reviewSequence < attempt.Sequence) {
            Assert.Equal(1, result.StrongStreak); Assert.Equal(7, result.Stability); Assert.Equal(DateTimeOffset.Parse("2026-04-04T11:00:00Z"), result.DueAt);
            Assert.Equal("content-score-v1", result.SchedulingRuleVersion);
        } else {
            Assert.Equal(0, result.StrongStreak); Assert.Equal(10d / 1440, result.Stability); Assert.Equal(at.AddMinutes(10), result.DueAt);
            Assert.Equal("manual-interval-v1", result.SchedulingRuleVersion);
        }
    }

    [Fact]
    public void Pure_projection_without_eligible_events_returns_the_original_baseline() {
        var at = LearningMutationFixture.At; var id = Guid.NewGuid(); var baseline = new LearningItem(id, LearningDomain.French, "Synthetic prompt", "Synthetic answer", 2, 5, at, at);
        var request = Guid.NewGuid(); var included = new LearningReview(request, new(Guid.NewGuid(), id, ReviewRating.Again, TimeSpan.FromSeconds(2), at), baseline);
        Assert.Same(baseline, TrainingProjection.Project(id, baseline, new HashSet<Guid> { request }, [included], new Dictionary<Guid, long>(), []));
    }

    [Fact]
    public void Pure_projection_rejects_a_baseline_from_another_question() {
        var at = LearningMutationFixture.At; var baseline = new LearningItem(Guid.NewGuid(), LearningDomain.French, "Synthetic prompt", "Synthetic answer", 2, 5, at, at);
        var error = Assert.Throws<DomainException>(() => TrainingProjection.Project(Guid.NewGuid(), baseline, new HashSet<Guid>(), [], new Dictionary<Guid, long>(), []));
        Assert.Equal(409, error.StatusCode);
    }
}
