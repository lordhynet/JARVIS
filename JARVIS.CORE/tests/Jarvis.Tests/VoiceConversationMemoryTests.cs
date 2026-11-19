using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Intelligence;

namespace Jarvis.Tests;

public sealed class VoiceConversationMemoryTests
{
    [Fact]
    public void Memory_rolls_older_excerpts_as_actual_turns_arrive_and_ignores_tool_content()
    {
        var session = Fixture(35);
        session = session with { Messages = [.. session.Messages!, new(Guid.NewGuid(), "tool", "Hidden reference", DateTimeOffset.UtcNow)] };
        var saved = VoiceConversationMemory.RefreshSummary(session);
        var excerpts = saved.VoiceSummary!.EarlierTurns;
        Assert.Equal(8, excerpts.Count);
        Assert.Equal("Turn 8", excerpts[0].Text);
        Assert.Equal("Turn 15", excerpts[^1].Text);
        var recent = VoiceConversationMemory.Recent(saved);
        Assert.Equal(20, recent.Count);
        Assert.Equal("Turn 16", recent[0].Text);
        Assert.Equal("Turn 35", recent[^1].Text);
        Assert.Same(saved, VoiceConversationMemory.RefreshSummary(saved));
        var advanced = VoiceConversationMemory.RefreshSummary(saved with
        {
            Messages = [.. saved.Messages!, new(Guid.NewGuid(), ChatRoles.Assistant, "Turn 36", DateTimeOffset.UtcNow)]
        });
        Assert.Equal("Turn 9", advanced.VoiceSummary!.EarlierTurns[0].Text);
        Assert.Equal("Turn 16", advanced.VoiceSummary.EarlierTurns[^1].Text);
        Assert.DoesNotContain("Hidden reference", JsonSerializer.Serialize(advanced.VoiceSummary));
        Assert.DoesNotContain("Hidden reference", JsonSerializer.Serialize(VoiceConversationMemory.Recent(advanced)));
    }

    [Fact]
    public void Json_byte_limits_include_escaping_and_favour_the_newest_turns()
    {
        const string pattern = "🔑漢\"\\\n";
        var session = Fixture(40, _ => string.Concat(Enumerable.Repeat(pattern, 600)));
        var saved = VoiceConversationMemory.RefreshSummary(session);
        var recent = VoiceConversationMemory.Recent(saved);
        var earlier = saved.VoiceSummary!.EarlierTurns;
        Assert.Equal(4, recent.Count);
        Assert.Equal(3, earlier.Count);
        Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(recent).Length, 1, VoiceConversationMemory.RecentByteLimit);
        Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(earlier).Length, 1, VoiceConversationMemory.SummaryByteLimit);
        Assert.All(recent.Concat(earlier), t => Assert.DoesNotContain("\uFFFD", t.Text));
        Assert.EndsWith("…", recent[0].Text);
        Assert.True(recent[^1].Text.Length > recent[0].Text.Length);
    }

    [Fact]
    public void Short_turns_leave_room_for_the_latest_full_answer()
    {
        var explanation = new string('a', 1100);
        var session = Fixture(20, i => i == 20 ? explanation : $"Short turn {i}");
        var recent = VoiceConversationMemory.Recent(session);
        Assert.Equal(20, recent.Count);
        Assert.Equal(explanation, recent[^1].Text);
        Assert.Equal("Short turn 1", recent[0].Text);
    }

    [Fact]
    public void One_long_turn_leaves_room_for_its_three_followups()
    {
        var session = Fixture(20, i => i == 17 ? new string('a', 8000) : $"Short turn {i}");
        var recent = VoiceConversationMemory.Recent(session);
        Assert.EndsWith("…", recent[16].Text);
        Assert.Equal(new[] { "Short turn 18", "Short turn 19", "Short turn 20" }, recent.TakeLast(3).Select(t => t.Text));
    }

    private static ConversationSession Fixture(int count, Func<int, string>? text = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid(), ActivityType.General, "Fixture", SessionStatus.Active, null, null, now, now, [],
            Messages: Enumerable.Range(1, count).Select(i => new ChatMessage(Guid.NewGuid(),
                i % 2 == 0 ? ChatRoles.Assistant : ChatRoles.User, text?.Invoke(i) ?? $"Turn {i}", now)).ToArray());
    }
}
