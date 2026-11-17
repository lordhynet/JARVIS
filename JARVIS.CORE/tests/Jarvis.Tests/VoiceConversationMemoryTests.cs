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
    public void Json_byte_limits_include_escaping_and_keep_all_twenty_recent_turns()
    {
        const string pattern = "🔑漢\"\\\n";
        var session = Fixture(40, _ => string.Concat(Enumerable.Repeat(pattern, 600)));
        var saved = VoiceConversationMemory.RefreshSummary(session);
        var recent = VoiceConversationMemory.Recent(saved);
        var earlier = saved.VoiceSummary!.EarlierTurns;
        Assert.Equal(20, recent.Count);
        Assert.Equal(8, earlier.Count);
        Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(recent).Length, 1, VoiceConversationMemory.RecentByteLimit);
        Assert.InRange(JsonSerializer.SerializeToUtf8Bytes(earlier).Length, 1, VoiceConversationMemory.SummaryByteLimit);
        Assert.All(recent.Concat(earlier), t => Assert.DoesNotContain("\uFFFD", t.Text));
        Assert.EndsWith("…", recent[0].Text);
    }

    private static ConversationSession Fixture(int count, Func<int, string>? text = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new(Guid.NewGuid(), ActivityType.General, "Fixture", SessionStatus.Active, null, null, now, now, [],
            Messages: Enumerable.Range(1, count).Select(i => new ChatMessage(Guid.NewGuid(),
                i % 2 == 0 ? ChatRoles.Assistant : ChatRoles.User, text?.Invoke(i) ?? $"Turn {i}", now)).ToArray());
    }
}
