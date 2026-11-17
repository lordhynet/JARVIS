using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Jarvis.Orchestrator.Training;

namespace Jarvis.Tests;

public sealed class TrainingDrillTests
{
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-11-13T12:00Z"); }
    private sealed class Fixture
    {
        public readonly InMemoryRuntimeStore Store = new(); public readonly InMemorySessionStore Sessions = new(); public readonly Clock Clock = new();
        public readonly Guid SessionId = Guid.NewGuid();
        public TrainingDrillService Drills => new(Store, Sessions, Clock);
        public TrainingService Training => new(Store, Sessions, Clock);
        public async Task Init() => await Sessions.AddAsync(new(SessionId, ActivityType.General, "Guided practice", SessionStatus.Active, null, null, Clock.GetUtcNow(), Clock.GetUtcNow(), []));
        public SubmitTrainingAnswerRequest Answer(TrainingDrill drill) { var step = drill.Steps[drill.AttemptIds.Count]; return new(Guid.NewGuid(), SessionId, step.QuestionId, step.QuestionVersion, drill.Mode, "I would verify the invariant and test the failure boundary.", DrillId: drill.Id, StepIndex: step.Index); }
    }
    public static TheoryData<TrainingMode, int> Shapes => new()
    {
        { TrainingMode.RapidDrill, 1 }, { TrainingMode.ExplainSimply, 1 }, { TrainingMode.SpacedReview, 1 },
        { TrainingMode.DeepDive, 3 }, { TrainingMode.ArchitectureChallenge, 3 }, { TrainingMode.FailureInjection, 2 },
        { TrainingMode.ProductionIncident, 3 }, { TrainingMode.CodeReview, 2 }, { TrainingMode.RandomMixedInterview, 3 }
    };
    [Theory, MemberData(nameof(Shapes))]
    public async Task All_nine_modes_have_real_steps_and_save_sequentially(TrainingMode mode, int count)
    {
        var f = new Fixture(); await f.Init(); var q = QuestionBank.All[0];
        await f.Store.UpdateAsync(s => { s.Learning[q.Id] = new(q.Id, q.Domain, q.Prompt, q.Reference, 0, 5, f.Clock.GetUtcNow(), f.Clock.GetUtcNow()); return true; });
        var request = new StartTrainingDrillRequest(Guid.NewGuid(), f.SessionId, mode);
        var view = await f.Drills.StartAsync(request, default);
        Assert.Equal(count, view.Drill.Steps.Count); Assert.Equal(9, DrillCatalog.Modes.Count);
        Assert.All(view.Drill.Steps, step => { Assert.NotEmpty(step.Prompt); Assert.NotEmpty(step.Reference); Assert.StartsWith("https://", step.SourceUrl); });
        if (mode == TrainingMode.CodeReview) Assert.All(view.Drill.Steps, s => Assert.NotEmpty(s.CodeSnippet!));
        if (mode == TrainingMode.RandomMixedInterview) Assert.Equal(3, view.Drill.Steps.Select(s => QuestionBank.Get(s.QuestionId).Domain).Distinct().Count());
        for (var i = 0; i < count; i++)
        {
            var answer = f.Answer(view.Drill); var saved = await f.Training.SubmitAsync(answer, default);
            Assert.Equal(view.Drill.Id, saved.DrillId); Assert.Equal(i, saved.StepIndex); Assert.Empty(saved.Assessments);
            var retry = await f.Training.SubmitAsync(answer, default); Assert.Equal(saved.Id, retry.Id);
            view = await f.Drills.StartAsync(request, default); Assert.Equal(i + 1, view.Drill.AttemptIds.Count);
        }
        Assert.Equal("Completed", view.Drill.Status); Assert.Equal(count, view.Answers.Count);
        Assert.All(view.Answers, answer => { Assert.Null(answer.WordsPerMinute); Assert.Null(answer.AnswerDurationMs); });
    }
    [Fact]
    public async Task Concurrent_answers_have_one_winner_and_cannot_skip_or_forge_a_step()
    {
        var f = new Fixture(); await f.Init(); var view = await f.Drills.StartAsync(new(Guid.NewGuid(), f.SessionId, TrainingMode.DeepDive), default);
        var request = f.Answer(view.Drill);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => f.Training.SubmitAsync(request with { StepIndex = 1 }, default))).StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => f.Training.SubmitAsync(request with { QuestionId = QuestionBank.All.Last().Id }, default))).StatusCode);
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(async _ => {
            try { return await f.Training.SubmitAsync(request with { RequestId = Guid.NewGuid() }, default); } catch (DomainException error) { Assert.Equal(409, error.StatusCode); return null; }
        }));
        Assert.Single(results.OfType<TrainingAttempt>()); Assert.Single(await f.Store.ReadAsync(s => s.TrainingAttempts));
    }
    [Fact]
    public async Task Cancellation_revokes_future_steps_but_preserves_saved_answers()
    {
        var f = new Fixture(); await f.Init(); var view = await f.Drills.StartAsync(new(Guid.NewGuid(), f.SessionId, TrainingMode.ProductionIncident), default);
        await f.Training.SubmitAsync(f.Answer(view.Drill), default);
        var current = await f.Store.ReadAsync(s => s.TrainingDrills[view.Drill.Id]);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => f.Drills.CancelAsync(current.Id, 1, default))).StatusCode);
        var cancelled = await f.Drills.CancelAsync(current.Id, current.Revision, default); Assert.Single(cancelled.Answers);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => f.Training.SubmitAsync(f.Answer(current), default))).StatusCode);
    }
    [Fact]
    public async Task Review_requires_due_cards_and_unsupported_scenarios_are_rejected()
    {
        var f = new Fixture(); await f.Init();
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => f.Drills.StartAsync(new(Guid.NewGuid(), f.SessionId, TrainingMode.SpacedReview), default))).StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => f.Drills.StartAsync(new(Guid.NewGuid(), f.SessionId, TrainingMode.CodeReview, QuestionBank.All.Last().Id), default))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<DomainException>(() => f.Training.SubmitAsync(new(Guid.NewGuid(), f.SessionId, QuestionBank.All[0].Id, 1, TrainingMode.CodeReview, "An answer"), default))).StatusCode);
    }
    [Fact]
    public async Task Create_retries_do_not_reroll_mixed_questions_or_allow_changed_payloads()
    {
        var f = new Fixture(); await f.Init(); var request = new StartTrainingDrillRequest(Guid.NewGuid(), f.SessionId, TrainingMode.RandomMixedInterview);
        var first = await f.Drills.StartAsync(request, default); var retry = await f.Drills.StartAsync(request, default);
        Assert.Equal(first.Drill.Steps, retry.Drill.Steps);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => f.Drills.StartAsync(request with { Mode = TrainingMode.DeepDive }, default))).StatusCode);
    }
}
