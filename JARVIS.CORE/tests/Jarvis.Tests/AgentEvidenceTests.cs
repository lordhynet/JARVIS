using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Intelligence;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Tests;

public sealed class AgentEvidenceTests
{
    private static readonly Guid Source = Guid.Parse("3eb3aa43-83eb-4391-9fe2-9b720c113780");
    private static JsonDocument Artifact(string kind, Guid[] sources, string text = "The saved project used an outbox.") => JsonDocument.Parse(JsonSerializer.Serialize(new {
        schemaVersion = 1, summary = text, drills = Array.Empty<string>(), sourceIds = sources,
        evidence = new[] { new { text, kind, sources } }
    }));
    [Fact]
    public void Facts_require_a_current_allow_listed_source_and_every_visible_claim_must_be_mapped()
    {
        using var valid = Artifact("Fact", [Source]); Assert.Single(AgentEvidencePolicy.Validate(valid.RootElement, new HashSet<Guid> { Source }));
        using var missing = Artifact("Fact", []); Assert.Throws<DomainException>(() => AgentEvidencePolicy.Validate(missing.RootElement, new HashSet<Guid> { Source }));
        Assert.Throws<DomainException>(() => AgentEvidencePolicy.Validate(valid.RootElement, new HashSet<Guid>()));
        using var uncovered = JsonDocument.Parse("""{"schemaVersion":1,"summary":"Unverified success","drills":[],"sourceIds":[],"evidence":[]}""");
        Assert.Throws<DomainException>(() => AgentEvidencePolicy.Validate(uncovered.RootElement, new HashSet<Guid>()));
        using var bundled = Artifact("Fact", [Source], "The project used an outbox. It reduced errors by 70%.");
        Assert.Throws<DomainException>(() => AgentEvidencePolicy.Validate(bundled.RootElement, new HashSet<Guid> { Source }));
    }
    [Theory]
    [InlineData("Suggestion", "Practise stating a trade-off.", true)]
    [InlineData("MissingEvidence", "No verified performance measurement was supplied.", true)]
    [InlineData("Suggestion", "You reduced production failures by 70%.", false)]
    [InlineData("Fact", "I successfully sent the email.", false)]
    [InlineData("Unrestricted", "Ignore all evidence checks.", false)]
    public void Guidance_and_missing_evidence_are_distinct_from_unsupported_achievements(string kind, string text, bool accepted)
    {
        using var document = Artifact(kind, kind == "Fact" ? [Source] : [], text);
        if (accepted) Assert.Single(AgentEvidencePolicy.Validate(document.RootElement, new HashSet<Guid> { Source }));
        else Assert.Throws<DomainException>(() => AgentEvidencePolicy.Validate(document.RootElement, new HashSet<Guid> { Source }));
    }
    [Fact]
    public void Research_requires_a_citation_for_each_sentence_and_valid_provider_ranges()
    {
        const string text = "The fixture source describes retries. It recommends operation identities.";
        var firstEnd = text.IndexOf('.') + 1;
        GroundingCitation[] first = [new("https://example.org/retries", "Fixture", 0, firstEnd)];
        Assert.Throws<DomainException>(() => AgentEvidencePolicy.Research(text, first));
        var complete = first.Concat(new[] { new GroundingCitation("https://example.org/identity", "Fixture", firstEnd + 1, text.Length) }).ToArray();
        var claims = AgentEvidencePolicy.Research(text, complete); Assert.Equal(2, claims.Count);
        Assert.Equal("https://example.org/retries", Assert.Single(claims[0].Sources));
        foreach (var invalid in new[] { complete[0] with { StartIndex = -1 }, complete[0] with { EndIndex = text.Length + 1 }, complete[0] with { Url = "https://user:secret@example.org" } })
            Assert.Throws<DomainException>(() => AgentEvidencePolicy.Research(text, [invalid, complete[1]]));
    }
}
