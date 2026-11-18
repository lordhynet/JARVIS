using System.Text.Json;
using System.Text.RegularExpressions;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Intelligence;

public sealed record AgentEvidenceClaim(string Text, string Kind, IReadOnlyList<string> Sources);

// Checks structure, full claim coverage and membership in canonical supplied evidence.
// Reference membership is not independent verification that a source entails a claim.
public static class AgentEvidencePolicy
{
    private static MatchCollection ClaimSpans(string text) => Regex.Matches(text, @"\S[\s\S]*?(?:[.!?](?=\s|$)|\r?\n|$)", RegexOptions.CultureInvariant);
    public static IReadOnlyList<string> ClaimTexts(string text) => ClaimSpans(text).Select(m => m.Value.Trim()).Where(t => t.Length > 0).ToArray();
    public static IReadOnlyList<AgentEvidenceClaim> Validate(JsonElement artifact, IReadOnlySet<Guid> allowed)
    {
        try { return ValidateCore(artifact, allowed); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException) {
            throw new DomainException(502, "Agent evidence does not match the installed schema.");
        }
    }
    private static IReadOnlyList<AgentEvidenceClaim> ValidateCore(JsonElement artifact, IReadOnlySet<Guid> allowed)
    {
        Guard.Require(artifact.ValueKind == JsonValueKind.Object && artifact.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() == artifact.EnumerateObject().Count(), 502, "Duplicate artifact fields are forbidden.");
        Guard.Require(artifact.GetProperty("schemaVersion").GetInt32() == 1 && artifact.GetProperty("drills").GetArrayLength() <= 3, 502, "Agent artifact schema is invalid.");
        var summary = Guard.Text(artifact.GetProperty("summary").GetString(), "Agent summary", 5000);
        var drills = artifact.GetProperty("drills").EnumerateArray().Select(x => Guard.Text(x.GetString(), "Drill", 1000)).ToArray();
        var visible = new[] { summary }.Concat(drills).SelectMany(ClaimTexts).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        Guard.Require(visible.Count is > 0 and <= 100, 502, "Artifact claim count exceeded its bound.");
        Guard.Require(artifact.TryGetProperty("evidence", out var evidence) && evidence.ValueKind == JsonValueKind.Array && evidence.GetArrayLength() == visible.Count,
            502, "Every summary and drill requires an explicit evidence classification.");
        var result = new List<AgentEvidenceClaim>(); var seen = new HashSet<string>(StringComparer.Ordinal); var used = new HashSet<Guid>();
        foreach (var item in evidence.EnumerateArray()) {
            Guard.Require(item.ValueKind == JsonValueKind.Object && item.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).SequenceEqual(new[] { "kind", "sources", "text" }), 502, "Evidence schema is invalid.");
            var text = Guard.Text(item.GetProperty("text").GetString(), "Evidence claim", 5000);
            var kind = item.GetProperty("kind").GetString();
            Guard.Require(visible.Contains(text) && seen.Add(text) && kind is "Fact" or "Inference" or "Suggestion" or "MissingEvidence", 502, "Evidence does not map to a unique visible claim.");
            var references = item.GetProperty("sources").EnumerateArray().Select(x => x.GetGuid()).ToArray();
            Guard.Require(references.Length <= 12 && references.Distinct().Count() == references.Length && references.All(allowed.Contains), 502, "Claim cites a source outside its allow-list.");
            Guard.Require(kind is not ("Fact" or "Inference") || references.Length > 0, 502, "A factual claim or inference has no allowed source.");
            Guard.Require(kind is not ("Suggestion" or "MissingEvidence") || !UnsupportedAchievement(text), 502, "An achievement claim cannot be relabelled as unsupported guidance.");
            Guard.Require(kind != "MissingEvidence" || Regex.IsMatch(text, @"\b(no|not|missing|unknown|unavailable|cannot|need|needs|require|requires)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), 502, "A missing-evidence statement must state its limitation.");
            Guard.Require(!Regex.IsMatch(text, @"\b(I|we|JARVIS)\s+(?:have\s+)?(?:successfully\s+)?(?:sent|booked|purchased|transferred|executed|deleted|deployed)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                502, "Model text cannot claim an external action completed.");
            used.UnionWith(references); result.Add(new(text, kind!, references.Select(id => id.ToString("D")).ToArray()));
        }
        var declared = artifact.GetProperty("sourceIds").EnumerateArray().Select(x => x.GetGuid()).ToArray();
        Guard.Require(declared.Distinct().Count() == declared.Length && used.SetEquals(declared), 502, "Artifact source IDs differ from its claim mappings.");
        return result.AsReadOnly();
    }
    private static bool UnsupportedAchievement(string text) => Regex.IsMatch(text,
        @"\b(you|your|I|we|my|our)\b.{0,80}\b(achieved|increased|reduced|saved|earned|delivered|built|led|managed|won)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<AgentEvidenceClaim> Research(string text, IReadOnlyList<GroundingCitation> citations)
    {
        Guard.Require(!string.IsNullOrWhiteSpace(text) && text.Length <= 16000 && citations.Count is > 0 and <= 30, 502, "Research requires bounded grounded evidence.");
        foreach (var citation in citations) Guard.Require(Uri.TryCreate(citation.Url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0 &&
            !uri.IsLoopback && Uri.CheckHostName(uri.Host) == UriHostNameType.Dns && !uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) &&
            citation.StartIndex >= 0 && citation.EndIndex > citation.StartIndex && citation.EndIndex <= text.Length && citation.Title.Length <= 500,
            502, "Research citation has an invalid URL, title or text range.");
        var claims = new List<AgentEvidenceClaim>();
        // Sentence/line boundaries form a deliberately conservative publication unit.
        foreach (Match match in ClaimSpans(text)) {
            var claim = match.Value.Trim(); if (claim.Length == 0) continue;
            var sources = citations.Where(c => c.StartIndex < match.Index + match.Length && c.EndIndex > match.Index)
                .Select(c => c.Url).Distinct(StringComparer.Ordinal).ToArray();
            Guard.Require(sources.Length > 0, 502, "Research contains a claim without an allowed citation.");
            claims.Add(new(claim, "ProviderGroundedClaim", sources));
        }
        Guard.Require(claims.Count is > 0 and <= 100, 502, "Research claim count exceeded its bound.");
        return claims.AsReadOnly();
    }
}
