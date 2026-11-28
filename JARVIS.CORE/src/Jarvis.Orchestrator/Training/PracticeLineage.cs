using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Intelligence;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Training;

// One personal source the realtime model was actually shown, with a version that can be re-checked at commit.
public sealed record PracticeContextSource(string Kind, Guid Id, string Version);

// What a realtime conversation has exposed to the model so far. Unverifiable is set when anything was shown whose freshness
// cannot be re-checked (for example a context section such as goals or CV); generation then refuses (review R4 item 1).
public sealed record PracticeContextManifest(Guid SessionId, IReadOnlyList<PracticeContextSource> Sources, bool Unverifiable,
    DateTimeOffset UpdatedAt);

public static class PracticeLineage
{
    public const string Story = "story", Learning = "learning";
    public const int MaximumSources = 200;

    // Cards have no revision, so their version is a fingerprint of everything that defines their content and provenance.
    public static string CardVersion(LearningItem card) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        card.Id, card.Domain, card.Prompt, card.Answer, card.SourceRecordingId, card.RecordedQuestionId, card.GeneratedQuestionId
    })));

    public static IReadOnlyList<PracticeContextSource> FromSelection(RuntimeState state, SelectedContext selected) =>
        selected.Stories.Select(s => new PracticeContextSource(Story, s.Id, s.Version.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .Concat(selected.Due.Where(c => state.Learning.ContainsKey(c.Id)).Select(c => new PracticeContextSource(Learning, c.Id, CardVersion(state.Learning[c.Id]))))
            .ToArray();

    // Accumulates exposure for a conversation. Sources only grow within a session; a version change is recorded as a new entry
    // so the manifest keeps every version the model may have seen.
    public static PracticeContextManifest Record(RuntimeState state, Guid sessionId, IEnumerable<PracticeContextSource> sources, DateTimeOffset at,
        bool unverifiable = false)
    {
        Guard.Require(sessionId != Guid.Empty, 400, "A conversation is required.");
        var current = state.PracticeContextManifests.GetValueOrDefault(sessionId) ?? new(sessionId, [], false, at);
        var merged = current.Sources.Concat(sources.Where(s => s.Kind is Story or Learning && s.Id != Guid.Empty && s.Version.Length > 0))
            .Distinct().Take(MaximumSources + 1).ToArray();
        // Beyond the bound the manifest can no longer describe everything shown, so it becomes unverifiable rather than truncated.
        var manifest = new PracticeContextManifest(sessionId, merged.Take(MaximumSources).ToArray(),
            current.Unverifiable || unverifiable || merged.Length > MaximumSources, at);
        state.PracticeContextManifests[sessionId] = manifest;
        return manifest;
    }

    // At the canonical commit: every exposed source must still exist at the exact version the model saw.
    public static bool Current(RuntimeState state, PracticeContextManifest manifest) => !manifest.Unverifiable && manifest.Sources.All(source => source.Kind switch
    {
        Story => state.Stories.TryGetValue(source.Id, out var story) && story.Version.ToString(System.Globalization.CultureInfo.InvariantCulture) == source.Version,
        Learning => state.Learning.TryGetValue(source.Id, out var card) && CardVersion(card) == source.Version,
        _ => false
    });

    public static bool Exposes(PracticeContextManifest manifest, string kind, Guid id) => manifest.Sources.Any(s => s.Kind == kind && s.Id == id);

    public static string LineageJson(PracticeContextManifest manifest) => JsonSerializer.Serialize(manifest.Sources);
}
