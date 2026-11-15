using System.Text.RegularExpressions;
using Jarvis.Contracts;

namespace Jarvis.Orchestrator.Coaching;

public static partial class TextCoach
{
    [GeneratedRegex(@"\b[\p{L}\p{N}]+(?:['’][\p{L}\p{N}]+)*\b", RegexOptions.CultureInvariant)] private static partial Regex Words();
    [GeneratedRegex(@"\b(?:um|uh|erm|basically|kind\s+of|sort\s+of)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)] private static partial Regex Fillers();
    public static CoachingAnalysis Analyse(string text, double? seconds)
    {
        var words = Words().Matches(text).Select(m => m.Value).ToArray();
        var sentences = Regex.Split(text, @"[.!?\r\n]+").Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        var fillers = Fillers().Matches(text).Select(m => m.Value).ToArray();
        var singular = words.Count(x => x.Equals("I", StringComparison.OrdinalIgnoreCase) || x.StartsWith("I'", StringComparison.OrdinalIgnoreCase) || x.StartsWith("I’", StringComparison.OrdinalIgnoreCase));
        var plural = words.Count(x => x.Equals("we", StringComparison.OrdinalIgnoreCase) || x.StartsWith("we'", StringComparison.OrdinalIgnoreCase) || x.StartsWith("we’", StringComparison.OrdinalIgnoreCase));
        var observations = new List<TextObservation>(); var next = new List<string>();
        if (fillers.Length > 0) { observations.Add(new("filler-candidates", "Possible filler phrases in the text; context may make them meaningful.", fillers.Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList())); next.Add("Repeat the opening without unnecessary filler phrases."); }
        var longSentences = sentences.Where(x => Words().Matches(x).Count > 35).Take(3).Select(x => Excerpt(x.Trim())).ToList();
        if (longSentences.Count > 0) { observations.Add(new("long-sentence", "A sentence exceeds 35 words. Consider splitting it; this is a text heuristic, not a speech diagnosis.", longSentences)); next.Add("Practise a point-first update with shorter sentences."); }
        if (plural > 0 && singular == 0) { observations.Add(new("ownership-language", "The answer uses 'we' without 'I'. For an ownership question, explain your own action while retaining team credit.", ["we"])); next.Add("Retell the story with your contribution and the team's contribution clearly separated."); }
        if (words.Length > 180) { observations.Add(new("long-answer", "The text exceeds 180 words. Check whether every detail answers the question; no elapsed speaking time is inferred.", [])); next.Add("Retell the answer using Frame → Problem → Ownership → Action → Outcome → Stop."); }
        if (words.Length < 12) observations.Add(new("limited-evidence", "This short answer supplies limited material for text observations. Its correctness and confidence are unknown.", []));
        if (next.Count == 0) next.Add("Compare your opening with the question, then practise one follow-up using a concrete example.");
        return new(words.Length, sentences.Length, fillers.Length, singular, plural, seconds,
            seconds is > 0 ? Math.Round(words.Length * 60d / seconds.Value, 1) : null,
            seconds is null ? "unavailable" : "user_reported_elapsed", observations, next.Distinct().Take(4).ToList(), "text-coach-v1");
    }
    private static string Excerpt(string value) => value.Length <= 240 ? value : value[..240];
}
