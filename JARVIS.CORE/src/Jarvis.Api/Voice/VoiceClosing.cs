using System.Text;

namespace Jarvis.Api.Voice;

// A code-owned matcher over the Core-captured user transcript. The model cannot end a session:
// only an utterance made entirely of closing words (plus a few filler words) counts as a goodbye.
internal static class VoiceClosing
{
    public static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan MaximumFarewellAudio = TimeSpan.FromSeconds(12);
    public static readonly TimeSpan MaximumFarewellWait = TimeSpan.FromSeconds(15);

    public const string FarewellInstruction = "The user just said goodbye. Reply with one short, warm farewell sentence. " +
        "Do not ask a question, offer more help or continue the conversation. Core ends the conversation after your reply.";
    public const string IdleInstruction = "Nobody has spoken for a while. Say only a brief line such as \"I'll be here.\" " +
        "Do not ask a question. Core ends the conversation after your reply.";

    public static readonly IReadOnlyList<string> DefaultPhrases =
    [
        "thank you", "thanks", "thank you very much", "thanks very much", "thanks a lot", "many thanks", "cheers",
        "that's all", "that's all from me", "that's all for now", "that's all for today", "that's it", "that's it for now",
        "that will be all", "that'll be all", "nothing else", "i'm done", "we're done", "i'm good",
        "bye", "bye bye", "goodbye", "good bye", "bye for now", "later", "see you", "see you later", "see ya",
        "talk later", "talk to you later", "speak later", "speak soon", "catch you later", "good night", "goodnight",
        "stop listening", "you can stop listening"
    ];

    // Words that may surround closing phrases without changing their meaning.
    private static readonly HashSet<string> Fillers = new(StringComparer.Ordinal)
    {
        "jarvis", "ok", "okay", "alright", "right", "perfect", "lovely", "brilliant", "oh", "and", "well", "so", "then", "now", "no", "mate"
    };

    private static readonly IReadOnlyList<string[]> Defaults = Compile(DefaultPhrases);

    public static bool IsClosing(string? transcript) => IsClosing(transcript, Defaults);

    public static bool IsClosing(string? transcript, IReadOnlyList<string[]> phrases)
    {
        if (string.IsNullOrWhiteSpace(transcript) || transcript.Length > 200) return false;
        var tokens = Tokens(transcript);
        if (tokens.Length is 0 or > 24) return false;
        var position = 0; var matched = false;
        while (position < tokens.Length)
        {
            var best = 0;
            foreach (var phrase in phrases)
                if (phrase.Length > best && position + phrase.Length <= tokens.Length && Matches(tokens, position, phrase)) best = phrase.Length;
            if (best > 0) { matched = true; position += best; continue; }
            if (!Fillers.Contains(tokens[position])) return false;
            position++;
        }
        return matched;
    }

    public static IReadOnlyList<string[]> Compile(IEnumerable<string> phrases) =>
        phrases.Select(Tokens).Where(p => p.Length > 0).Distinct(TokenComparer.Instance).ToArray();

    private static bool Matches(string[] tokens, int start, string[] phrase)
    {
        for (var i = 0; i < phrase.Length; i++) if (tokens[start + i] != phrase[i]) return false;
        return true;
    }

    private static string[] Tokens(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var raw in text.Normalize(NormalizationForm.FormKC))
        {
            // Apostrophes are dropped so "that's" and "thats" match the same phrase.
            if (raw is '\'' or '’' or '‘' or '`') continue;
            var c = char.ToLowerInvariant(raw);
            builder.Append(char.IsLetterOrDigit(c) ? c : ' ');
        }
        return builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private sealed class TokenComparer : IEqualityComparer<string[]>
    {
        public static readonly TokenComparer Instance = new();
        public bool Equals(string[]? x, string[]? y) => x is not null && y is not null && x.SequenceEqual(y);
        public int GetHashCode(string[] value) => string.Join(' ', value).GetHashCode(StringComparison.Ordinal);
    }

    // The idle rule is pure so it can be tested without waiting a minute.
    public static bool IsIdle(TimeSpan sinceActivity, bool responsePending, bool closing) =>
        !closing && !responsePending && sinceActivity >= IdleAfter;

    public static TimeSpan GraceAfterFarewell(double farewellAudioSeconds) =>
        Grace + TimeSpan.FromSeconds(Math.Clamp(farewellAudioSeconds, 0, MaximumFarewellAudio.TotalSeconds));
}
