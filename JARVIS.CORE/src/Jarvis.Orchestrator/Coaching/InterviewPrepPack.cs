using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Coaching;

/// <summary>One code-owned competency the prep pack can recognise in an advert and in Story Bank stories.</summary>
public sealed record JobCompetency(string Key, string Title, Regex Pattern, string Behavioural, string Technical);

/// <summary>
/// B3-5 v0: a deterministic interview prep pack from an imported job advert. No AI call, no research, no storage:
/// it is recomputed on read, so deleting the advert removes everything derived from it. Advert text is untrusted
/// third-party data. It is only matched against a fixed, code-owned competency list; it is never executed,
/// summarised by a model, turned into memory or domain knowledge, or used to propose an action. Story matches are
/// keyword overlaps, not evidence that a claim is true.
/// </summary>
public static class InterviewPrepPackBuilder
{
    public const int MaxStoriesPerRequirement = 3, MaxQuestions = 20, QuoteLength = 200;
    private static Regex R(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    public static IReadOnlyList<JobCompetency> Catalogue { get; } = Array.AsReadOnly<JobCompetency>([
        new("csharp-dotnet", "C# and .NET", R(@"(?<![\w])C#|\.NET\b|\bASP\.NET\b|\bdotnet\b"),
            "Tell me about a .NET service you owned end to end. What did you change and why?",
            "How would you find and fix a memory leak or thread-pool starvation in a .NET service?"),
        new("distributed-systems", "Distributed systems", R(@"\bdistributed systems?\b|\bmicroservices?\b|\bevent[- ]driven\b|\bmessage (?:queue|broker)s?\b|\bKafka\b|\bRabbitMQ\b|\bService Bus\b"),
            "Describe a time a distributed system failed in a way you did not expect. How did you find the cause?",
            "How do you make a message consumer idempotent, and what happens on redelivery?"),
        new("databases", "Databases and SQL", R(@"\bSQL\b|\bPostgreSQL\b|\bSQL Server\b|\bdatabases?\b|\bNoSQL\b|\bMongoDB\b|\bCosmos ?DB\b"),
            "Tell me about a database change you shipped safely to production.",
            "How would you diagnose a query that became slow after a data volume increase?"),
        new("cloud", "Cloud platforms", R(@"\bAWS\b|\bAzure\b|\bGCP\b|\bGoogle Cloud\b|\bcloud\b"),
            "Describe a cloud workload you designed or operated. What would you do differently?",
            "How do you keep a cloud service available across a zone failure, and what does it cost?"),
        new("api-design", "API design", R(@"\bREST(?:ful)?\b|\bAPIs?\b|\bgRPC\b|\bGraphQL\b"),
            "Tell me about an API you designed that other teams depended on.",
            "How do you version an API without breaking existing clients?"),
        new("system-design", "System design and architecture", R(@"\bsystem design\b|\barchitect(?:ure|ural)?\b|\bscalab(?:le|ility)\b|\bhigh availability\b"),
            "Describe an architecture decision you made, the trade-off and how it turned out.",
            "Design a service that has to handle ten times the current traffic. Where does it break first?"),
        new("payments", "Payments and fintech", R(@"\bpayments?\b|\bfintech\b|\bbanking\b|\bledgers?\b|\breconciliation\b|\bcard schemes?\b"),
            "Tell me about a time money or a ledger was at risk. What did you do?",
            "How do you guarantee a payment is not charged twice when a request times out?"),
        new("security", "Security", R(@"\bsecurity\b|\bsecure\b|\bOAuth\b|\bOpenID\b|\bauthenti(?:cation|cate)\b|\bOWASP\b|\bencryption\b"),
            "Describe a security issue you found or fixed. How did you limit the impact?",
            "Walk me through how you would protect an API from token theft and replay."),
        new("testing", "Testing and quality", R(@"\bunit tests?\b|\bintegration tests?\b|\bTDD\b|\btest automation\b|\btesting\b"),
            "Tell me about a bug your tests did not catch. What did you change afterwards?",
            "How do you test code that depends on time, the network or a database?"),
        new("production", "Production support and observability", R(@"\bon[- ]call\b|\bincidents?\b|\bobservability\b|\bmonitoring\b|\bSRE\b|\bproduction support\b"),
            "Describe an incident you led or supported. What was the root cause and what changed?",
            "Which signals would you alert on for a new service, and how do you avoid noisy alerts?"),
        new("devops", "CI/CD and infrastructure", R(@"\bCI/CD\b|\bDocker\b|\bKubernetes\b|\bTerraform\b|\bpipelines?\b|\bDevOps\b"),
            "Tell me about a deployment that went wrong. How did you recover?",
            "How would you design a safe rollout and rollback for a schema change?"),
        new("leadership", "Leadership and mentoring", R(@"\bmentor(?:ing|ship)?\b|\bleadership\b|\btech(?:nical)? lead\b|\blead (?:engineer|developer)\b"),
            "Tell me about someone you helped grow. What did you do and what changed for them?",
            "How do you lead a technical decision when senior engineers disagree?"),
        new("stakeholders", "Stakeholders and communication", R(@"\bstakeholders?\b|\bcommunicat(?:e|ion)\b|\bcross[- ]functional\b|\bcollaborat(?:e|ion|ive)\b"),
            "Describe a time you had to explain a technical risk to a non-technical stakeholder.",
            "How do you negotiate scope when a deadline cannot move?"),
        new("delivery", "Delivery and ownership", R(@"\bagile\b|\bscrum\b|\bownership\b|\bend[- ]to[- ]end\b|\bdeliver(?:y|ing)?\b"),
            "Tell me about a project you owned from idea to production.",
            "How do you break a large piece of work into safe, shippable steps?")
    ]);

    /// <param name="sourceQuotes">Attached CV quotes per story, used by the readiness check; missing means none.</param>
    public static InterviewPrepPack Build(ImportedDocument advert, IEnumerable<Story> stories, IReadOnlyDictionary<Guid, int>? sourceQuotes = null)
    {
        Guard.Require(advert is { DeletedAt: null, Kind: "job-advert" }, 404, "Job advert not found.");
        ArgumentNullException.ThrowIfNull(stories);
        var storyText = stories.Select(s => (s.Id, s.Content.Title, Text: StoryText(s.Content),
            Ready: StoryReadinessChecker.Check(s, sourceQuotes?.GetValueOrDefault(s.Id) ?? 0))).ToArray();
        var requirements = new List<PrepRequirement>();
        foreach (var competency in Catalogue) {
            var match = competency.Pattern.Match(advert.Text);
            if (!match.Success) continue;
            var matched = storyText.Where(s => competency.Pattern.IsMatch(s.Text))
                .OrderBy(s => s.Title, StringComparer.Ordinal).ThenBy(s => s.Id)
                .Take(MaxStoriesPerRequirement).Select(s => new PrepStoryMatch(s.Id, Clean(s.Title, 80), s.Ready.Score, s.Ready.Level)).ToArray();
            requirements.Add(new(competency.Key, competency.Title, Quote(advert.Text, match.Index, match.Length), matched, matched.Length == 0,
                matched.Length > 0 && matched.All(m => m.Level == "draft")));
        }
        // Gaps first, then requirements backed only by draft stories: those are what to prepare before the interview.
        var questions = requirements.OrderByDescending(r => r.Gap).ThenByDescending(r => r.NeedsWork).SelectMany(r => {
            var competency = Catalogue.Single(x => x.Key == r.Key);
            return new[] { competency.Behavioural, competency.Technical };
        }).Take(MaxQuestions).ToArray();
        var fingerprint = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new { Version = 1, advert.Id, advert.Revision, advert.ContentHash, Questions = questions })));
        return new(advert.Id, advert.Title, requirements.AsReadOnly(), questions, requirements.Count(r => r.Gap),
            "Matched by keywords against a fixed competency list. Advert text is untrusted third-party data; story matches are overlaps, not verification. No AI was used.", fingerprint);
    }

    private static string StoryText(StoryContent c) => string.Join("\n", new[] {
        c.Title, c.CompanyProject, c.ServiceFeature, c.Context, c.Problem, c.Constraints, c.PersonalOwnership, c.TechnicalDecisions,
        c.TradeOffs, c.Implementation, c.Collaboration, c.FailureModes, c.Result, c.Metrics, c.ChangeToday }.Concat(c.CvClaims ?? []));

    // The sentence or line containing the match, bounded; returned to the owner as data only.
    private static string Quote(string text, int index, int length)
    {
        // A sentence ends at a newline, or at . ! ? followed by whitespace or the end (so ".NET" and "8.0" don't split).
        bool Stop(int i) => text[i] == '\n' || text[i] is '.' or '!' or '?' && (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1]));
        var start = index;
        while (start > 0 && !Stop(start - 1)) start--;
        var end = Math.Min(text.Length, index + length);
        while (end < text.Length && !Stop(end)) end++;
        if (end < text.Length && text[end] != '\n') end++; // keep the closing punctuation
        return Clean(text[start..end], QuoteLength);
    }

    private static string Clean(string value, int max)
    {
        var text = string.Concat(value.Select(c => char.IsControl(c) ? ' ' : c)).Trim();
        return text.Length <= max ? text : text[..max].TrimEnd() + "…";
    }
}
