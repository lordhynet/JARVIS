using Jarvis.Contracts;
using Jarvis.Orchestrator.Coaching;
using Jarvis.Orchestrator.Domains;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Tests;

public sealed class ReviewRound2Tests
{
    [Fact]
    public void Domain_disable_preserves_terminal_history_and_uses_supplied_clock()
    {
        var state = new RuntimeState(); var now = new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero);
        var statuses = Enum.GetValues<AgentTaskStatus>(); var ids = statuses.Select(_ => Guid.NewGuid()).ToArray(); var runId = Guid.NewGuid();
        state.AgentRuns[runId] = new(runId, null, "fixture", "practice", 1, false, now.AddHours(-1), now.AddHours(1), "Running", ids,
            WorkflowPolicy: new("1", false, false, true, null, "education"));
        for (var i = 0; i < ids.Length; i++) state.AgentTasks[ids[i]] = new(ids[i], runId, null, AgentRole.CommunicationCoach, 1,
            "fixture", statuses[i], 1, now.AddHours(-1), ClaimToken: Guid.NewGuid(), Output: "Saved history", CompletedAt: now.AddMinutes(-10));
        var old = state.AgentTasks.ToDictionary();
        DomainExecutionLifecycle.DisableRuns(state, new HashSet<string> { "education" }, now);
        Assert.Equal("Cancelled", state.AgentRuns[runId].Status);
        foreach (var id in ids) {
            var active = old[id].Status is AgentTaskStatus.Queued or AgentTaskStatus.Running or AgentTaskStatus.RetryScheduled or AgentTaskStatus.AwaitingApproval or AgentTaskStatus.AwaitingReceipt;
            if (active) { Assert.Equal(AgentTaskStatus.Cancelled, state.AgentTasks[id].Status); Assert.Null(state.AgentTasks[id].ClaimToken); Assert.Equal(now, state.AgentTasks[id].CompletedAt); }
            else Assert.Equal(old[id], state.AgentTasks[id]);
        }
    }
    [Fact]
    public void A_bad_restored_timing_does_not_break_delivery_reads_and_write_validation_remains_strict()
    {
        var state = new RuntimeState(); var session = Guid.NewGuid(); var bad = Guid.NewGuid(); var good = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        state.VoiceTurns[bad] = new(bad, session, ChatRoles.User, "I chose an approach.", 10, now, Timing: new(10, 11, SpeechDelivery.PcmQuietMethod));
        state.VoiceTurns[good] = new(good, session, ChatRoles.User, "We measured it.", 10, now.AddSeconds(1), Timing: new(10, 2, SpeechDelivery.PcmQuietMethod));
        Assert.Throws<DomainException>(() => VoiceTranscriptRules.Validate(state.VoiceTurns[bad]));
        var report = Assert.Single(SpeechDelivery.Sessions(state)); Assert.Equal(2, report.Turns); Assert.Equal(.2, report.PauseRatio);
        Assert.Null(report.Trend.Single(t => t.TurnId == bad).PauseRatio); Assert.Equal(.2, report.Trend.Single(t => t.TurnId == good).PauseRatio);
        Assert.Equal(11, state.VoiceTurns[bad].Timing!.QuietSeconds);
    }
}
