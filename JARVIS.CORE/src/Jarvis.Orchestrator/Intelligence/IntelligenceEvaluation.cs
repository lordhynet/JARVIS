using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Intelligence;

public sealed record IntelligenceEvaluationCase(string Id, string Category, string Input, JsonElement Recorded,
    string[] RequiredTerms, string[] BannedTerms, string? ExpectedTool = null, string? ExpectedDecision = null,
    Guid[]? SourceIds = null, string[]? SourceUrls = null, decimal MaximumUsd = .02m, decimal RecordedCostUsd = .0001m);
public sealed record IntelligenceEvaluationResult(string Id, bool Passed, IReadOnlyList<string> Failures, decimal CostUsd);
public sealed record IntelligenceEvaluationReport(Guid Id, string CorpusVersion, string Mode, int Passed, int Failed,
    decimal CostUsd, IReadOnlyList<IntelligenceEvaluationResult> Cases);
public sealed record RunIntelligenceEvaluationRequest(Guid RequestId, bool RunPaid = false, bool AllowCloudProcessing = false);
public interface IIntelligenceEvaluator
{
    Task<IntelligenceEvaluationReport> RunAsync(RunIntelligenceEvaluationRequest request, BudgetActor actor, CancellationToken ct);
}

// The same deterministic quality gate checks recorded fixtures and optional future provider runs.
// Its fixtures are synthetic. Passing it is not a claim of measured live model quality.
public static class IntelligenceEvaluation
{
    public const string CorpusVersion = "intelligence-offline-v1";
    public const string ManageScope = "jarvis:intelligence:evaluate";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static IReadOnlyList<IntelligenceEvaluationCase> Corpus()
    {
        using var stream = typeof(IntelligenceEvaluation).Assembly.GetManifestResourceStream("Jarvis.Intelligence.offline-v1.json")
            ?? throw new InvalidOperationException("The offline evaluation corpus is not packaged.");
        return JsonSerializer.Deserialize<IntelligenceEvaluationCase[]>(stream, Json) ?? throw new InvalidOperationException("Invalid offline evaluation corpus.");
    }
    public static IntelligenceEvaluationResult Check(IntelligenceEvaluationCase test, string output, decimal costUsd)
    {
        var failures = new List<string>();
        if (costUsd < 0 || costUsd > test.MaximumUsd) failures.Add("cost_limit");
        if (output.Length > 20000) return new(test.Id, false, ["output_bound"], costUsd);
        try {
            using var parsed = JsonDocument.Parse(output, new JsonDocumentOptions { MaxDepth = 10 }); var root = parsed.RootElement;
            string visible;
            switch (test.Category) {
                case "coaching":
                    Guard.Require(root.GetProperty("schemaVersion").GetInt32() == 1 && root.GetProperty("drills").GetArrayLength() <= 3 &&
                        !root.TryGetProperty("actions", out _), 502, "Unexpected coaching structure or action.");
                    var evidence = AgentEvidencePolicy.Validate(root, (test.SourceIds ?? []).ToHashSet());
                    visible = string.Join("\n", evidence.Select(e => e.Text));
                    break;
                case "research":
                    visible = root.GetProperty("text").GetString()!;
                    var citations = root.GetProperty("citations").Deserialize<GroundingCitation[]>(Json)!;
                    Guard.Require(citations.All(c => (test.SourceUrls ?? []).Contains(c.Url, StringComparer.Ordinal)), 502, "Research source is outside its fixture allow-list.");
                    AgentEvidencePolicy.Research(visible, citations);
                    break;
                case "tool":
                case "refusal":
                    Guard.Require(root.ValueKind == JsonValueKind.Object && root.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)
                        .SequenceEqual(new[] { "capability", "decision", "reason" }), 502, "Tool selection structure is invalid.");
                    if (root.GetProperty("decision").GetString() != test.ExpectedDecision || root.GetProperty("capability").GetString() != test.ExpectedTool) failures.Add("tool_choice");
                    visible = Guard.Text(root.GetProperty("reason").GetString(), "Evaluation reason", 1000);
                    break;
                default: throw new DomainException(502, "Unknown evaluation category.");
            }
            if (test.RequiredTerms.Any(term => !visible.Contains(term, StringComparison.OrdinalIgnoreCase))) failures.Add("required_content");
            if (test.BannedTerms.Any(term => visible.Contains(term, StringComparison.OrdinalIgnoreCase))) failures.Add("banned_claim");
        }
        catch (Exception error) when (error is DomainException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException or NullReferenceException) {
            failures.Add("structure_or_evidence");
        }
        return new(test.Id, failures.Count == 0, failures.AsReadOnly(), costUsd);
    }
    public static IntelligenceEvaluationReport Offline(Guid id)
    {
        var results = Corpus().Select(c => Check(c, c.Recorded.GetRawText(), c.RecordedCostUsd)).ToArray();
        return new(id, CorpusVersion, "offline-recorded-synthetic", results.Count(c => c.Passed), results.Count(c => !c.Passed), results.Sum(c => c.CostUsd), results);
    }
}
