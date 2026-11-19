using System.Text;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Domains;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;

namespace Jarvis.Orchestrator.Intelligence;

public static partial class ContextSelector
{
    public static IReadOnlyList<ContextMemoryItem> SelectMemory(RuntimeState state, string domainKey, string query, DateTimeOffset now,
        int maximumBytes = 6000)
    {
        Guard.Require(maximumBytes is >= 256 and <= 16000, 400, "Memory context bound is invalid.");
        var terms = Terms(query); var result = new List<ContextMemoryItem>(); var bytes = 0;
        foreach (var item in state.ContextMemories.Values.Where(m => m.DomainKey == domainKey && m.Sensitivity != MemorySensitivity.Sensitive &&
            m.ExpiresAt > now && ContextMemoryService.Current(state, m))
            .OrderByDescending(m => Score(m.Text, terms)).ThenByDescending(m => m.RecordedAt).ThenBy(m => m.Id)) {
            var size = Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(item));
            if (bytes + size > maximumBytes) continue;
            result.Add(item); bytes += size; if (result.Count == 5) break;
        }
        return result.AsReadOnly();
    }
}

// Owner-authored context uses the existing runtime, selector, privacy boundary and D2 transaction.
// This service never accepts writes from a model and never sends Sensitive memory to a provider.
public sealed class ContextMemoryService(IDomainRegistry domains, ICoreTransaction transaction, IRuntimeStore runtime, TimeProvider clock)
{
    public static string Version(ContextMemoryItem item) => PrivacyData.Hash(JsonSerializer.Serialize(item));
    public static bool Current(RuntimeState state, ContextMemoryItem item) => item.Source.Kind == "note"
        ? item.Source.Id == item.Id && item.Source.Version == PrivacyData.Hash(item.Text)
        : item.Source.Kind is "voice" or "import" && AgentRuntime.SourceCurrent(state, item.Source);
    public async Task<ContextMemoryItem> SaveAsync(string key, SaveContextMemoryRequest request, CancellationToken ct = default)
    {
        Guard.Require(CoreInvocationContext.Origin == InvocationOrigin.ManualClient, 403, "Only the owner can save domain memory.");
        Guard.Require(request.RequestId != Guid.Empty && request.Sensitivity is MemorySensitivity.Personal or MemorySensitivity.Sensitive,
            400, "Owner memory must be classified Personal or Sensitive.");
        var resolved = await domains.ResolveActiveAsync(key, ct);
        Guard.Require(resolved is not null, 409, "Activate the domain before saving memory.");
        using var epoch = PrivacyContext.Enter(resolved.Epoch);
        return await transaction.ExecuteAsync(async writes => {
            var active = await writes.ReadActiveDomainAsync(key);
            Guard.Require(active is not null && active.Version.Id == resolved.Version.Id && active.Definition.Revision == resolved.Definition.Revision,
                409, "Domain changed while saving memory.");
            var state = writes.Runtime; var now = clock.GetUtcNow();
            Guard.Require(request.ExpiresAt > now && request.ExpiresAt <= now.AddDays(90), 400, "Memory must expire within 90 days.");
            string text; AgentSourceRef source; Guid? session = null;
            if (request.SourceKind == "note") {
                Guard.Require(request.SourceId is null && request.Start == 0 && request.Length == 0, 400, "A note has no external source range.");
                text = Guard.Text(request.Text, "Memory note", 1200); source = new("note", request.RequestId, PrivacyData.Hash(text));
            } else {
                Guard.Require(request.SourceId is { } id && id != Guid.Empty && request.Text is null, 400, "Choose a canonical source, without replacement text.");
                var sourceId = request.SourceId!.Value; string original; string version;
                if (request.SourceKind == "voice") {
                    Guard.Require(state.VoiceTurns.TryGetValue(sourceId, out var turn) && turn.Role == ChatRoles.User && !turn.Restricted &&
                        !state.RetiredSessions.Contains(turn.SessionId), 409, "Restricted, retired or missing speech cannot become memory.");
                    original = turn.Text; version = PrivacyData.Hash(JsonSerializer.Serialize(turn)); session = turn.SessionId;
                } else if (request.SourceKind == "import") {
                    Guard.Require(state.ImportedDocuments.TryGetValue(sourceId, out var document) && document.DeletedAt is null && document.Kind != "interview", 409, "Interview transcripts cannot become memory or domain knowledge.");
                    original = document.Text; version = document.ContentHash;
                } else throw new DomainException(400, "Memory sources are owner notes, captured speech or stored imports.");
                Guard.Require(request.Start >= 0 && request.Length is > 0 and <= 1200 && request.Start <= original.Length - request.Length, 400, "Memory source range is invalid.");
                text = original.Substring(request.Start, request.Length); source = new(request.SourceKind, sourceId, version);
            }
            if (state.ContextMemories.TryGetValue(request.RequestId, out var prior)) {
                Guard.Require(prior.DomainKey == key && prior.Text == text && prior.Source == source && prior.ExpiresAt == request.ExpiresAt && prior.Sensitivity == request.Sensitivity,
                    409, "Memory ID was reused with different content."); return prior;
            }
            Guard.Require(state.ContextMemories.Count < 2000, 409, "Memory storage limit reached. Delete or expire older notes first.");
            var item = new ContextMemoryItem(request.RequestId, key, active.Version.Id, text, source, session, request.Sensitivity, now, request.ExpiresAt);
            state.ContextMemories.Add(item.Id, item); return item;
        }, ct);
    }
    public async Task<IReadOnlyList<ContextMemoryItem>> ListAsync(string key, CancellationToken ct = default)
    {
        Guard.Require(CoreInvocationContext.Origin == InvocationOrigin.ManualClient, 403, "Memory browsing requires the owner dashboard.");
        await domains.PreviewAsync(key, null, ct);
        return await runtime.ReadAsync(s => (IReadOnlyList<ContextMemoryItem>)s.ContextMemories.Values.Where(m => m.DomainKey == key &&
            m.ExpiresAt > clock.GetUtcNow() && Current(s, m)).OrderByDescending(m => m.RecordedAt).ToArray(), ct);
    }
    public Task<bool> DeleteAsync(string key, Guid id, CancellationToken ct = default) => runtime.UpdateAsync(s => {
        Guard.Require(CoreInvocationContext.Origin == InvocationOrigin.ManualClient, 403, "Only the owner can delete domain memory.");
        Guard.Require(s.ContextMemories.TryGetValue(id, out var item) && item.DomainKey == key, 404, "Memory not found.");
        AgentRuntime.InvalidateSource(s, "memory", id); return s.ContextMemories.Remove(id);
    }, ct);
    public static void DropSource(RuntimeState state, string kind, Guid id)
    {
        foreach (var item in state.ContextMemories.Values.Where(m => m.Source.Kind == kind && m.Source.Id == id).ToArray()) {
            AgentRuntime.InvalidateSource(state, "memory", item.Id); state.ContextMemories.Remove(item.Id);
        }
    }
}
