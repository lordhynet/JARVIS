using Jarvis.Contracts;
using Jarvis.Orchestrator.Training;

namespace Jarvis.Tests;

public sealed class PracticeOutlineTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-28T12:00:00Z");
    private static readonly TimeSpan Window = TimeSpan.FromDays(30);
    private static TrainingAttempt Attempt(Guid question, DateTimeOffset at, bool skipped = false, bool deleted = false) =>
        new(Guid.NewGuid(), Guid.NewGuid(), question, 1, TrainingMode.RapidDrill, 1, 1, skipped, at, [], [], DeletedAt: deleted ? at : null);

    [Fact]
    public void Every_authored_practice_topic_has_a_valid_outline_and_nothing_else_does()
    {
        Assert.Equal(TrainingDrillService.Topics.OrderBy(t => t), PracticeOutlines.ByTopic.Keys.OrderBy(t => t));
        Assert.All(PracticeOutlines.ByTopic.Values, entries =>
        {
            Assert.InRange(entries.Count, 3, 12);
            Assert.Equal(entries.Count, entries.Select(e => e.Key).Distinct().Count());
            Assert.All(entries, e => { Assert.InRange(e.Key.Length, 1, 64); Assert.Equal(1, e.Version); Assert.Equal(e.Key.ToLowerInvariant(), e.Key); });
        });
        Assert.False(PracticeOutlines.ByTopic.ContainsKey(LearningDomain.French));
    }

    [Fact]
    public void Authored_questions_come_first_until_each_has_a_recent_retained_answer()
    {
        var questions = QuestionBank.All.Where(q => q.Domain == LearningDomain.SystemDesign).ToArray();
        Assert.False(PracticeOutlines.AuthoredExhausted([], LearningDomain.SystemDesign, Now, Window));
        Assert.False(PracticeOutlines.AuthoredExhausted([Attempt(questions[0].Id, Now)], LearningDomain.SystemDesign, Now, Window));
        var all = questions.Select(q => Attempt(q.Id, Now.AddDays(-1))).ToArray();
        Assert.True(PracticeOutlines.AuthoredExhausted(all, LearningDomain.SystemDesign, Now, Window));
    }

    [Theory]
    [InlineData("old")]
    [InlineData("skipped")]
    [InlineData("deleted")]
    public void Old_skipped_or_deleted_answers_do_not_count_as_covering_authored_questions(string kind)
    {
        var questions = QuestionBank.All.Where(q => q.Domain == LearningDomain.Databases).ToArray();
        var attempts = questions.Select((q, i) => i == 0
            ? Attempt(q.Id, kind == "old" ? Now.AddDays(-31) : Now, skipped: kind == "skipped", deleted: kind == "deleted")
            : Attempt(q.Id, Now)).ToArray();
        Assert.False(PracticeOutlines.AuthoredExhausted(attempts, LearningDomain.Databases, Now, Window));
    }

    [Fact]
    public void The_next_entry_is_the_least_practised_with_outline_order_breaking_ties()
    {
        var outline = PracticeOutlines.ByTopic[LearningDomain.SystemDesign];
        Assert.Equal(outline[0], PracticeOutlines.NextEntry(LearningDomain.SystemDesign, new Dictionary<string, int>()));
        var counts = outline.ToDictionary(e => e.Key, _ => 2); counts[outline[3].Key] = 1;
        Assert.Equal(outline[3], PracticeOutlines.NextEntry(LearningDomain.SystemDesign, counts));
        Assert.Null(PracticeOutlines.NextEntry(LearningDomain.French, new Dictionary<string, int>()));
    }
}
