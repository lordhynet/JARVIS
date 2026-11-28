using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Training;

namespace Jarvis.Tests;

// Pure generated-drill state transitions (design R3-2, review R4 item 2). Synthetic content; no storage or provider.
public sealed class GeneratedDrillFlowTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-28T12:00:00Z");
    private static readonly OutlineEntry Scaling = new("scaling", 1), Consistency = new("consistency", 1);
    private static PendingBinding Binding(Guid? utterance = null, long epoch = 0, string route = "r1") => new(utterance ?? Guid.NewGuid(), epoch, route);
    private static GeneratedQuestionContent Question(params string[] followUps) =>
        new(Guid.NewGuid(), 1, "Sharding a hot table", "How would you shard a write-heavy table?", "Partition keys and hot spots.", followUps);
    private static TrainingDrill Start(TrainingMode mode = TrainingMode.RapidDrill, PendingBinding? binding = null) =>
        GeneratedDrillFlow.Start(Guid.NewGuid(), Guid.NewGuid(), mode, LearningDomain.SystemDesign, Scaling, binding ?? Binding(), At);
    private static TrainingDrill Answer(TrainingDrill drill) => drill with { AttemptIds = [.. drill.AttemptIds, Guid.NewGuid()] };

    [Fact]
    public void Starting_creates_an_active_drill_awaiting_its_first_question()
    {
        var drill = Start();
        Assert.Equal("Active", drill.Status); Assert.Empty(drill.Steps); Assert.True(GeneratedDrillFlow.AwaitingQuestion(drill));
        Assert.Equal(new PendingNeed(1, 0, "scaling", 1, drill.Generated!.Pending!.TriggerUtteranceId, 0, "r1"), drill.Generated.Pending);
        Assert.Equal(LearningDomain.SystemDesign, drill.RequestedTopic);
    }

    [Fact]
    public void Authored_drills_keep_their_JSON_and_are_never_changed_by_the_flow()
    {
        var authored = new TrainingDrill(Guid.NewGuid(), Guid.NewGuid(), TrainingMode.RapidDrill, null, 1, "Active", At, [], []);
        Assert.DoesNotContain("Generated", JsonSerializer.Serialize(authored));
        Assert.Same(authored, GeneratedDrillFlow.AfterAnswer(authored, Consistency, Binding(), true));
        Assert.False(GeneratedDrillFlow.AwaitingQuestion(authored));
    }

    [Theory]
    [InlineData(TrainingMode.ArchitectureChallenge)]
    [InlineData(TrainingMode.CodeReview)]
    [InlineData(TrainingMode.RandomMixedInterview)]
    public void Unsupported_styles_are_refused_rather_than_switched(TrainingMode mode) =>
        Assert.Equal(400, Assert.Throws<DomainException>(() => Start(mode)).StatusCode);

    [Fact]
    public void A_rapid_drill_question_becomes_one_step_bound_to_its_generated_question()
    {
        var drill = Start(); var q = Question("ignored for rapid drill");
        var next = GeneratedDrillFlow.ApplyQuestion(drill, drill.Generated!.Pending!, q);
        var step = Assert.Single(next.Steps);
        Assert.Equal(q.QuestionId, step.GeneratedQuestionId); Assert.Equal(0, step.Index);
        Assert.Null(next.Generated!.Pending); Assert.Equal(1, next.Generated.QuestionsPrepared); Assert.Equal(2, next.Revision);
        Assert.False(GeneratedDrillFlow.AwaitingQuestion(next));
    }

    [Fact]
    public void A_deep_dive_keeps_every_follow_up_in_one_group_before_asking_for_the_next_question()
    {
        var drill = Start(TrainingMode.DeepDive);
        drill = GeneratedDrillFlow.ApplyQuestion(drill, drill.Generated!.Pending!, Question("hot shards", "rebalancing"));
        Assert.Equal(3, drill.Steps.Count); Assert.All(drill.Steps, s => Assert.NotNull(s.GeneratedQuestionId));
        drill = Answer(drill); Assert.Null(GeneratedDrillFlow.AfterAnswer(drill, Consistency, Binding(), true).Generated!.Pending);
        drill = Answer(drill); Assert.Null(GeneratedDrillFlow.AfterAnswer(drill, Consistency, Binding(), true).Generated!.Pending);
        drill = GeneratedDrillFlow.AfterAnswer(Answer(drill), Consistency, Binding(), true);
        Assert.Equal("Active", drill.Status);
        Assert.Equal(("consistency", 3), (drill.Generated!.Pending!.OutlineKey, drill.Generated.Pending.StepIndex));
        Assert.True(GeneratedDrillFlow.AwaitingQuestion(drill));
    }

    [Fact]
    public void Two_rapid_questions_continue_without_completing_the_drill()
    {
        var drill = Start();
        drill = GeneratedDrillFlow.AfterAnswer(Answer(GeneratedDrillFlow.ApplyQuestion(drill, drill.Generated!.Pending!, Question())), Consistency, Binding(), true);
        drill = GeneratedDrillFlow.ApplyQuestion(drill, drill.Generated!.Pending!, Question());
        Assert.Equal(2, drill.Steps.Count); Assert.Equal(2, drill.Generated!.QuestionsPrepared); Assert.Equal("Active", drill.Status);
    }

    public static TheoryData<string, Func<PendingNeed, PendingNeed>> StaleNeeds => new()
    {
        { "revision", p => p with { DrillRevision = p.DrillRevision + 1 } },
        { "step", p => p with { StepIndex = 5 } },
        { "outline", p => p with { OutlineKey = "other" } },
        { "utterance", p => p with { TriggerUtteranceId = Guid.NewGuid() } },
        { "epoch", p => p with { PrivacyEpoch = 7 } },
        { "route", p => p with { RouteRevision = "r2" } }
    };

    [Theory]
    [MemberData(nameof(StaleNeeds))]
    public void A_response_bound_to_a_stale_need_is_refused(string _, Func<PendingNeed, PendingNeed> stale)
    {
        var drill = Start();
        Assert.Equal(409, Assert.Throws<DomainException>(() => GeneratedDrillFlow.ApplyQuestion(drill, stale(drill.Generated!.Pending!), Question())).StatusCode);
    }

    [Fact]
    public void A_second_generator_for_the_same_need_is_refused_after_the_first_commits()
    {
        var drill = Start(); var need = drill.Generated!.Pending!;
        var first = GeneratedDrillFlow.ApplyQuestion(drill, need, Question());
        Assert.Equal(409, Assert.Throws<DomainException>(() => GeneratedDrillFlow.ApplyQuestion(first, need, Question())).StatusCode);
    }

    [Fact]
    public void A_newer_utterance_refreshes_the_need_so_old_responses_fail_and_the_user_is_not_stuck()
    {
        var a = Guid.NewGuid(); var drill = Start(binding: Binding(a)); var old = drill.Generated!.Pending!;
        var refreshed = GeneratedDrillFlow.RefreshBinding(drill, Binding(Guid.NewGuid(), route: "r1"));
        Assert.True(GeneratedDrillFlow.AwaitingQuestion(refreshed));
        Assert.Equal((old.StepIndex, old.OutlineKey), (refreshed.Generated!.Pending!.StepIndex, refreshed.Generated.Pending.OutlineKey));
        Assert.Equal(409, Assert.Throws<DomainException>(() => GeneratedDrillFlow.ApplyQuestion(refreshed, old, Question())).StatusCode);
        Assert.Single(GeneratedDrillFlow.ApplyQuestion(refreshed, refreshed.Generated.Pending, Question()).Steps);
        Assert.Same(refreshed, GeneratedDrillFlow.RefreshBinding(refreshed, new(refreshed.Generated.Pending.TriggerUtteranceId, 0, "r1")));
    }

    [Theory]
    [InlineData("route")]
    [InlineData("epoch")]
    public void A_route_change_or_handoff_epoch_while_pending_refreshes_the_binding(string change)
    {
        var u = Guid.NewGuid(); var drill = Start(binding: Binding(u));
        var refreshed = GeneratedDrillFlow.RefreshBinding(drill, change == "route" ? Binding(u, route: "r2") : Binding(u, epoch: 1));
        Assert.Equal(drill.Revision + 1, refreshed.Revision);
        Assert.Equal(409, Assert.Throws<DomainException>(() => GeneratedDrillFlow.ApplyQuestion(refreshed, drill.Generated!.Pending!, Question())).StatusCode);
    }

    [Fact]
    public void Stop_clears_the_need_and_a_late_generator_can_never_reopen_the_drill()
    {
        var drill = Start(); var need = drill.Generated!.Pending!;
        var stopped = GeneratedDrillFlow.Cancel(drill);
        Assert.Equal("Cancelled", stopped.Status); Assert.Null(stopped.Generated!.Pending); Assert.Equal(GeneratedDrillFlow.EndCancelled, stopped.Generated.EndReason);
        Assert.Equal(409, Assert.Throws<DomainException>(() => GeneratedDrillFlow.ApplyQuestion(stopped, need, Question())).StatusCode);
        Assert.Same(stopped, GeneratedDrillFlow.Cancel(stopped));
        Assert.False(GeneratedDrillFlow.AwaitingQuestion(stopped));
    }

    [Fact]
    public void Generation_committed_before_stop_is_kept_and_then_stopped()
    {
        var drill = Start();
        var generated = GeneratedDrillFlow.ApplyQuestion(drill, drill.Generated!.Pending!, Question());
        var stopped = GeneratedDrillFlow.Cancel(generated);
        Assert.Single(stopped.Steps); Assert.Equal("Cancelled", stopped.Status);
    }

    [Theory]
    [InlineData(false, true, GeneratedDrillFlow.EndQuota)]
    [InlineData(true, false, GeneratedDrillFlow.EndOutlineExhausted)]
    public void Reaching_a_limit_or_the_end_of_the_outline_completes_the_drill_with_a_reason(bool quotaAvailable, bool hasNext, string reason)
    {
        var drill = Start();
        drill = Answer(GeneratedDrillFlow.ApplyQuestion(drill, drill.Generated!.Pending!, Question()));
        var ended = GeneratedDrillFlow.AfterAnswer(drill, hasNext ? Consistency : null, Binding(), quotaAvailable);
        Assert.Equal("Completed", ended.Status); Assert.Equal(reason, ended.Generated!.EndReason); Assert.Null(ended.Generated.Pending);
        Assert.Equal(409, Assert.Throws<DomainException>(() => GeneratedDrillFlow.AfterAnswer(ended, Consistency, Binding(), true)).StatusCode);
    }

    [Fact]
    public void Prepared_steps_are_counted_but_never_claimed_as_heard()
    {
        // R3-4: QuestionsPrepared counts committed questions; nothing records presentation without delivery evidence.
        var drill = Start(TrainingMode.DeepDive);
        drill = GeneratedDrillFlow.ApplyQuestion(drill, drill.Generated!.Pending!, Question("a", "b"));
        Assert.Equal(1, drill.Generated!.QuestionsPrepared);
        Assert.DoesNotContain(typeof(GeneratedDrillState).GetProperties(), p => p.Name.Contains("Present") || p.Name.Contains("Heard") || p.Name.Contains("Delivered"));
    }
}
