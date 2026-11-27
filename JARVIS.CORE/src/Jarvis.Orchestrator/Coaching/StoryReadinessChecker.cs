using System.Text.RegularExpressions;
using Jarvis.Contracts;

namespace Jarvis.Orchestrator.Coaching;

/// <summary>
/// Deterministic interview-readiness checklist for a Story Bank story. No AI and no storage: it reads the story's own
/// fields and how many source quotes are attached. It checks shape (context, personal ownership, decisions, trade-offs,
/// result, measurement, failure modes, reflection, evidence), never truth. Following the Story Bank rule "blank metrics
/// are better than invented ones", an explicit "not measured" counts as ready and the advice never asks for invented facts.
/// </summary>
public static class StoryReadinessChecker
{
    public const int MaxFollowUps = 6;
    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(200);
    private static readonly Regex FirstPerson = new(@"\b(I|I'm|I've|I'd|my|me|myself)\b", RegexOptions.CultureInvariant, Limit);
    private static readonly Regex TeamOnly = new(@"\b(we|our|us|the team)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Limit);
    private static readonly Regex Comparison = new(@"\b(instead|rather|alternative|option|trade-?off|cost|because|versus|vs\.?|rejected|chose|over)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Limit);
    private static readonly Regex HonestUnknown = new(@"\b(not measured|unmeasured|unknown|no (?:data|metric)s?|not tracked)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, Limit);

    public static StoryReadiness Check(Story story, int sourceQuotes)
    {
        ArgumentNullException.ThrowIfNull(story);
        var c = story.Content;
        var checks = new List<StoryReadinessCheck>();
        var followUps = new List<string>();
        void Add(string key, string label, string status, string advice, string? followUp = null)
        {
            checks.Add(new(key, label, status, advice));
            if (status != "ready" && followUp is not null) followUps.Add(followUp);
        }
        bool Has(string value, int min = 12) => !string.IsNullOrWhiteSpace(value) && value.Trim().Length >= min;

        Add("situation", "Context and problem", Has(c.Context) || Has(c.Problem) ? "ready" : "missing",
            "Say where this happened and what was wrong, in one or two sentences.", "What was the situation, and what problem had to be solved?");

        var ownership = c.PersonalOwnership;
        Add("ownership", "Personal ownership",
            !Has(ownership) ? "missing" : FirstPerson.IsMatch(ownership) ? "ready" : TeamOnly.IsMatch(ownership) ? "weak" : "ready",
            "Separate what you personally did from what the team did; interviewers probe for \"I\".",
            "What exactly did you personally own or decide, as opposed to the team?");

        Add("decisions", "Technical decisions", Has(c.TechnicalDecisions) ? "ready" : "missing",
            "Name the key technical decision you made.", "Which technical decision mattered most, and why did you make it?");

        Add("trade-offs", "Trade-offs", !Has(c.TradeOffs) ? "missing" : Comparison.IsMatch(c.TradeOffs) ? "ready" : "weak",
            "Name the alternative you rejected and what you gave up.", "Which alternative did you reject, and what did your choice cost?");

        Add("result", "Result", Has(c.Result, 8) ? "ready" : "missing",
            "State the outcome plainly, including partial success.", "What actually happened as a result?");

        var metrics = c.Metrics;
        Add("measurement", "Measurement",
            !Has(metrics, 3) ? "missing" : metrics.Any(char.IsDigit) || HonestUnknown.IsMatch(metrics) ? "ready" : "weak",
            "Add a real measurement if you have one; otherwise write \"not measured\" so the gap is explicit. Never invent a number.",
            "Do you have a real measurement for the outcome? If not, can you say so plainly?");

        Add("failure", "Failure modes", Has(c.FailureModes) ? "ready" : "missing",
            "Say what could have gone wrong, or did, and how you handled it.", "What failed or could have failed, and how did you respond?");

        Add("reflection", "Reflection", Has(c.ChangeToday) ? "ready" : "missing",
            "Say what you would do differently today.", "Knowing what you know now, what would you change?");

        Add("evidence", "Source evidence", sourceQuotes > 0 ? "ready" : "weak",
            "Attach an exact quote from your CV or another source document to back the claim.", null);

        var score = (int)Math.Round(checks.Sum(x => x.Status switch { "ready" => 1.0, "weak" => 0.5, _ => 0.0 }) * 100 / checks.Count,
            MidpointRounding.AwayFromZero);
        var level = score >= 85 ? "interview-ready" : score >= 50 ? "usable" : "draft";
        return new(story.Id, story.Version, story.Content.Title, score, level, checks.AsReadOnly(),
            followUps.Take(MaxFollowUps).ToArray(), sourceQuotes);
    }
}
