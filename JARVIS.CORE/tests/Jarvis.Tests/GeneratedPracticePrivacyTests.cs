using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Training;

namespace Jarvis.Tests;

// Slice 2b-i: all-copy purge on runtime state, with synthetic fixtures. No caller exists yet.
public sealed class GeneratedPracticePrivacyTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-28T12:00:00Z");
    private const string Secret = "SECRET-PRIVATE-STORY-TEXT";

    private sealed record Fixture(RuntimeState State, Guid Question, Guid Attempt, Guid Drill, Guid Review, Guid Recall, Guid Receipt, Guid Operation);

    private static LearningItem Card(Guid id, Guid? generated) => new(id, LearningDomain.SystemDesign, $"Prompt {Secret}", $"Reference {Secret}",
        1, 5, At, At, GeneratedQuestionId: generated);

    private static Fixture Build(bool activeOnPurgedStep = true)
    {
        var s = new RuntimeState(); var q = Guid.NewGuid(); var session = Guid.NewGuid();
        var card = Card(q, q);
        s.Learning[q] = card; s.TrainingBaselines[q] = card;
        var review = Guid.NewGuid();
        s.Reviews[review] = new(review, new(Guid.NewGuid(), q, ReviewRating.Good, TimeSpan.FromSeconds(3), At), card with { MasteryScore = 3 });
        s.TrainingManualSequences[review] = 1;
        var recall = Guid.NewGuid();
        s.VoiceRecalls[recall] = new(recall, session, q, "fp", "Rating", Guid.NewGuid(), At, At);
        var receipt = Guid.NewGuid();
        s.CreationReceipts[receipt] = new(receipt, 0, LearningCreationReceipt.Created, 1, "content-fingerprint", card, null);
        var attempt = Guid.NewGuid();
        s.TrainingAttempts[attempt] = new(attempt, session, q, 1, TrainingMode.RapidDrill, 1, 1, false, At,
            [new(1, $"My answer {Secret}", "voice", At)], []);
        var operation = Guid.NewGuid();
        s.TrainingOperations[operation] = new("submit", attempt, "answer-derived-fingerprint");
        var drill = Guid.NewGuid();
        var step = new TrainingDrillStep(0, q, 1, $"Title {Secret}", $"Prompt {Secret}", $"Reference {Secret}", "", GeneratedQuestionId: q);
        s.TrainingDrills[drill] = new(drill, session, TrainingMode.RapidDrill, null, 1, "Active", At, [step],
            activeOnPurgedStep ? [] : [attempt]);
        return new(s, q, attempt, drill, review, recall, receipt, operation);
    }

    [Fact]
    public void Purge_removes_every_durable_copy_and_leaves_only_content_free_tombstones()
    {
        var f = Build();
        var purged = GeneratedPracticePrivacy.Purge(f.State, new HashSet<Guid> { f.Question }, At);
        Assert.Contains(f.Question, purged);
        var s = f.State;
        Assert.False(s.Learning.ContainsKey(f.Question)); Assert.False(s.TrainingBaselines.ContainsKey(f.Question));
        Assert.False(s.Reviews.ContainsKey(f.Review)); Assert.False(s.TrainingManualSequences.ContainsKey(f.Review));
        Assert.False(s.VoiceRecalls.ContainsKey(f.Recall));
        var receipt = s.CreationReceipts[f.Receipt]; Assert.Null(receipt.Result); Assert.Null(receipt.Fingerprint);
        var attempt = s.TrainingAttempts[f.Attempt];
        Assert.Empty(attempt.Answers); Assert.Empty(attempt.Assessments); Assert.Equal(At, attempt.DeletedAt);
        Assert.Equal("", s.TrainingOperations[f.Operation].Fingerprint);
        var step = Assert.Single(s.TrainingDrills[f.Drill].Steps);
        Assert.Equal(GeneratedPracticePrivacy.TombstoneText, step.Prompt); Assert.Equal(GeneratedPracticePrivacy.TombstoneText, step.Reference);
        Assert.Equal(GeneratedPracticePrivacy.TombstoneTitle, step.Title);
        // No retained runtime copy contains the private text.
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(new { s.Learning, s.TrainingBaselines, s.Reviews, s.VoiceRecalls, s.CreationReceipts,
            s.TrainingAttempts, s.TrainingOperations, s.TrainingDrills }));
    }

    [Fact]
    public void An_active_drill_waiting_on_a_purged_step_is_cancelled_and_cannot_be_answered()
    {
        var f = Build(activeOnPurgedStep: true);
        GeneratedPracticePrivacy.Purge(f.State, new HashSet<Guid> { f.Question }, At);
        var drill = f.State.TrainingDrills[f.Drill];
        Assert.Equal("Cancelled", drill.Status); Assert.Equal(2, drill.Revision);
    }

    [Fact]
    public void A_finished_step_is_tombstoned_without_changing_a_drill_that_no_longer_needs_it()
    {
        var f = Build(activeOnPurgedStep: false);   // the only step is already answered, so nothing is pending
        GeneratedPracticePrivacy.Purge(f.State, new HashSet<Guid> { f.Question }, At);
        Assert.Equal("Active", f.State.TrainingDrills[f.Drill].Status);
        Assert.Equal(GeneratedPracticePrivacy.TombstoneText, f.State.TrainingDrills[f.Drill].Steps[0].Prompt);
    }

    [Fact]
    public void Authored_interview_content_is_never_purged_even_if_a_caller_passes_its_ID()
    {
        var s = new RuntimeState(); var authored = QuestionBank.All[0]; var session = Guid.NewGuid();
        var card = Card(authored.Id, null); s.Learning[authored.Id] = card; s.TrainingBaselines[authored.Id] = card;
        var attempt = Guid.NewGuid();
        s.TrainingAttempts[attempt] = new(attempt, session, authored.Id, 1, TrainingMode.RapidDrill, 1, 1, false, At, [new(1, "answer", "voice", At)], []);
        var drill = Guid.NewGuid();
        s.TrainingDrills[drill] = new(drill, session, TrainingMode.RapidDrill, null, 1, "Active", At,
            [new(0, authored.Id, 1, authored.Title, authored.Prompt, authored.Reference, authored.SourceUrl)], []);
        var before = JsonSerializer.Serialize(new { s.Learning, s.TrainingBaselines, s.TrainingAttempts, s.TrainingDrills });
        Assert.Empty(GeneratedPracticePrivacy.Purge(s, new HashSet<Guid> { authored.Id, Guid.Empty }, At));
        Assert.Equal(before, JsonSerializer.Serialize(new { s.Learning, s.TrainingBaselines, s.TrainingAttempts, s.TrainingDrills }));
    }

    [Fact]
    public void Purge_is_idempotent_and_does_not_bump_revisions_twice()
    {
        var f = Build();
        GeneratedPracticePrivacy.Purge(f.State, new HashSet<Guid> { f.Question }, At);
        var once = JsonSerializer.Serialize(new { f.State.TrainingAttempts, f.State.TrainingOperations, f.State.CreationReceipts, f.State.TrainingDrills });
        var attemptRevision = f.State.TrainingAttempts[f.Attempt].Revision;
        GeneratedPracticePrivacy.Purge(f.State, new HashSet<Guid> { f.Question }, At);
        Assert.Equal(attemptRevision, f.State.TrainingAttempts[f.Attempt].Revision);
        Assert.Equal(once, JsonSerializer.Serialize(new { f.State.TrainingAttempts, f.State.TrainingOperations, f.State.CreationReceipts, f.State.TrainingDrills }));
    }

    [Fact]
    public void Unrelated_generated_and_authored_content_is_untouched()
    {
        var f = Build(); var other = Guid.NewGuid();
        f.State.Learning[other] = Card(other, other);
        GeneratedPracticePrivacy.Purge(f.State, new HashSet<Guid> { f.Question }, At);
        Assert.True(f.State.Learning.ContainsKey(other));
        Assert.Empty(GeneratedPracticePrivacy.Purge(f.State, new HashSet<Guid>(), At));
    }

    [Fact]
    public void Provenance_fields_are_omitted_from_JSON_for_authored_cards_and_steps()
    {
        Assert.DoesNotContain("GeneratedQuestionId", JsonSerializer.Serialize(Card(Guid.NewGuid(), null)));
        Assert.DoesNotContain("GeneratedQuestionId", JsonSerializer.Serialize(new TrainingDrillStep(0, Guid.NewGuid(), 1, "t", "p", "r", "u")));
        var id = Guid.NewGuid();
        Assert.Equal(id, JsonSerializer.Deserialize<LearningItem>(JsonSerializer.Serialize(Card(Guid.NewGuid(), id)))!.GeneratedQuestionId);
    }
}
