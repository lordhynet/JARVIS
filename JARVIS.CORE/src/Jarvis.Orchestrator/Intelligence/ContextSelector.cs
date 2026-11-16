using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jarvis.Contracts;

namespace Jarvis.Orchestrator.Intelligence;

public sealed record ContextStory(Guid Id, int Version, string Title, string PersonalOwnership, string TechnicalDecisions,
    string TradeOffs, string Result, string Metrics);
public sealed record ContextCard(Guid Id, LearningDomain Domain, string Prompt, DateTimeOffset DueAt);
public sealed record SelectedContext(IReadOnlyList<ContextStory> Stories, IReadOnlyList<ContextCard> Due, int Bytes);

/// <summary>Deterministic lexical retrieval. Source text is data; no model call, answer disclosure or new memory store.</summary>
public static class ContextSelector
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        { "the", "a", "an", "my", "me", "i", "to", "for", "with", "and", "of", "please", "story", "practise", "practice" };
    private static string Clip(string value, int max = 500) => value.Length <= max ? value : value[..max];
    private static string[] Terms(string query) => Regex.Matches(Clip(query, 4000).ToLowerInvariant(), @"[\p{L}\p{N}]+")
        .Select(x => x.Value).Where(x => x.Length > 1 && !StopWords.Contains(x)).Distinct(StringComparer.Ordinal).Take(40).ToArray();
    private static int Score(string text, string[] terms)
    {
        var words = Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]+").Select(x => x.Value).ToHashSet(StringComparer.Ordinal);
        return terms.Count(words.Contains);
    }
    public static SelectedContext Select(IEnumerable<Story> stories, IEnumerable<LearningItem> cards, string query,
        DateTimeOffset now, int maxBytes = 8000, int maxStories = 2, int maxCards = 3, bool requireStoryMatch = false)
    {
        if (maxBytes is < 256 or > 64000 || maxStories is < 0 or > 10 || maxCards is < 0 or > 10)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        var terms = Terms(query); var chosenStories = new List<ContextStory>(); var chosenCards = new List<ContextCard>();
        // Include the metadata field itself; ten digits conservatively cover the final byte count.
        int Size() => JsonSerializer.SerializeToUtf8Bytes(new { Stories = chosenStories, Due = chosenCards, Bytes = int.MaxValue }).Length;
        var ranked = stories.Select(x => new { Story = x, Score = 4 * Score(x.Content.Title, terms) +
            Score(x.Content.CompanyProject + " " + x.Content.ServiceFeature + " " + x.Content.PersonalOwnership + " " + x.Content.TechnicalDecisions + " " + x.Content.Result, terms) })
            .Where(x => !requireStoryMatch || terms.Length > 0 && x.Score > 0)
            .OrderByDescending(x => x.Score).ThenByDescending(x => x.Story.UpdatedAt).ThenBy(x => x.Story.Id);
        foreach (var entry in ranked)
        {
            if (chosenStories.Count >= maxStories) break;
            var x = entry.Story;
            chosenStories.Add(new(x.Id, x.Version, Clip(x.Content.Title, 120), Clip(x.Content.PersonalOwnership),
                Clip(x.Content.TechnicalDecisions), Clip(x.Content.TradeOffs), Clip(x.Content.Result), Clip(x.Content.Metrics)));
            if (Size() > maxBytes) chosenStories.RemoveAt(chosenStories.Count - 1);
        }
        foreach (var x in cards.Where(x => x.DueAt <= now).Select(x => new { Card = x, Score = Score(x.Prompt + " " + x.Domain, terms) })
            .OrderByDescending(x => x.Score).ThenBy(x => x.Card.MasteryScore ?? 0).ThenBy(x => x.Card.DueAt).ThenBy(x => x.Card.Id))
        {
            if (chosenCards.Count >= maxCards) break;
            chosenCards.Add(new(x.Card.Id, x.Card.Domain, Clip(x.Card.Prompt, 700), x.Card.DueAt));
            if (Size() > maxBytes) chosenCards.RemoveAt(chosenCards.Count - 1);
        }
        return new(chosenStories.AsReadOnly(), chosenCards.AsReadOnly(), Size());
    }
}
