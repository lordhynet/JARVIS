using System.Text.Json;
using Jarvis.Contracts;

namespace Jarvis.Orchestrator.Intelligence;

/// <summary>Bounded excerpts of actual conversation. Prepared answers and tool results are never sources.</summary>
public static class VoiceConversationMemory
{
    public const int RecentTurnLimit = 20;
    public const int RecentByteLimit = 5000;
    public const int SummaryTurnLimit = 8;
    public const int SummaryByteLimit = 1800;
    private const int RecentTextByteLimit = 1500;
    private const int SummaryTextByteLimit = 600;

    public static ConversationSession RefreshSummary(ConversationSession session)
    {
        var older = Turns(session).SkipLast(RecentTurnLimit).TakeLast(SummaryTurnLimit).ToArray();
        if (older.Length == 0)
            return session.VoiceSummary is null ? session : session with { VoiceSummary = null };

        var excerpts = Excerpts(older, SummaryByteLimit, SummaryTextByteLimit);
        return session.VoiceSummary?.EarlierTurns.SequenceEqual(excerpts) == true
            ? session : session with { VoiceSummary = new(excerpts) };
    }

    public static IReadOnlyList<VoiceMemoryTurn> Recent(ConversationSession session) =>
        Excerpts(Turns(session).TakeLast(RecentTurnLimit).ToArray(), RecentByteLimit, RecentTextByteLimit);

    private static IEnumerable<ChatMessage> Turns(ConversationSession session) =>
        (session.Messages ?? []).Where(turn => (turn.Role is ChatRoles.User or ChatRoles.Assistant)
            && !string.IsNullOrWhiteSpace(turn.Text));

    private static VoiceMemoryTurn[] Excerpts(ChatMessage[] turns, int byteLimit, int textByteLimit)
    {
        var excerpts = new List<VoiceMemoryTurn>(turns.Length);
        var remaining = byteLimit - 2; // JSON array brackets.
        foreach (var turn in turns.Reverse())
        {
            var empty = new VoiceMemoryTurn(turn.Role, "");
            var overhead = JsonSerializer.SerializeToUtf8Bytes(empty).Length + (excerpts.Count > 0 ? 1 : 0);
            var text = ClipJsonText(turn.Text, Math.Min(textByteLimit, remaining - overhead));
            if (text.Length == 0) break;
            excerpts.Add(empty with { Text = text });
            remaining -= overhead + EncodedBytes(text);
        }
        excerpts.Reverse();
        return excerpts.ToArray();
    }

    // Measure JSON escapes as well as UTF-8 bytes. Avoid cutting a surrogate pair.
    public static string ClipJsonText(string text, int byteLimit)
    {
        const string suffix = "…";
        if (EncodedBytes(text) <= byteLimit) return text;
        var available = byteLimit - EncodedBytes(suffix);
        if (available < 0) return "";
        var low = 0; var high = Math.Min(text.Length, available);
        while (low < high)
        {
            var middle = (low + high + 1) / 2;
            var end = CompletePrefix(text, middle);
            if (EncodedBytes(text[..end]) <= available) low = middle;
            else high = middle - 1;
        }
        return text[..CompletePrefix(text, low)] + suffix;
    }

    private static int EncodedBytes(string text) => JsonEncodedText.Encode(text).EncodedUtf8Bytes.Length;

    private static int CompletePrefix(string text, int count) =>
        count > 0 && count < text.Length && char.IsHighSurrogate(text[count - 1]) ? count - 1 : count;
}
