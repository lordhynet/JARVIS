using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Domains;

public sealed class DomainRegistryService(IDomainRegistryStore store, ICapabilityCatalog capabilities, TimeProvider clock) : IDomainRegistry
{
    private readonly SemaphoreSlim seeding = new(1, 1);
    private long seededEpoch = -1;
    private DateTimeOffset Now() { var value = clock.GetUtcNow(); return new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero); }
    private static void UniqueNames(DomainRegistryState state, DomainManifest manifest, Guid? ownId)
    {
        var names = DomainPolicy.Names(manifest);
        foreach (var other in state.Definitions.Values.Where(d => d.Id != ownId)) {
            var reserved = DomainPolicy.Names(state.Versions[other.LatestVersionId].Manifest);
            if (other.CurrentVersionId is Guid active) reserved = reserved.Concat(DomainPolicy.Names(state.Versions[active].Manifest)).ToHashSet(StringComparer.Ordinal);
            Guard.Require(!names.Overlaps(reserved) && other.WorkflowKey != DomainPolicy.WorkflowKey(manifest.Key), 409, "An equivalent domain or alias already exists: " + other.Key);
        }
    }
    private static DomainDefinition Definition(DomainRegistryState state, string key) =>
        state.Definitions.Values.SingleOrDefault(d => d.Key == key) ?? throw new DomainException(404, "Domain not found.");
    private DomainPreview Preview(DomainRegistryState state, DomainDefinition definition, DomainVersion version)
    {
        Guard.Require(version.DomainId == definition.Id && version.ManifestHash == DomainPolicy.Hash(version.Manifest), 503, "Domain version integrity check failed.");
        var gaps = DomainPolicy.Dependencies(version.Manifest, capabilities).ToList();
        if (version.Manifest.ParentKey is { } parent && !state.Definitions.Values.Any(d => d.Key == parent && d.Status == DomainStatus.Active)) gaps.Add("Parent domain is not active: " + parent);
        return new(definition, version, DomainPolicy.Risk(version.Manifest), gaps, "domain:" + definition.Key,
            version.Manifest.UsesPersonalContext, version.Manifest.ExecutionMode == DomainExecutionMode.Template ? 0 : null);
    }
    private async Task Seed(CancellationToken ct)
    {
        var epoch = await store.EpochAsync(ct); if (Interlocked.Read(ref seededEpoch) == epoch) return;
        await seeding.WaitAsync(ct);
        try {
            epoch = await store.EpochAsync(ct); if (Interlocked.Read(ref seededEpoch) == epoch) return;
            var applied = await store.UpdateAsync((state, currentEpoch) => {
                foreach (var installed in DomainCatalog.BuiltIns) {
                    if (state.Definitions.Values.Any(d => d.Key == installed.Key)) continue;
                    var manifest = DomainPolicy.BuiltIn(installed); var now = Now(); var hash = DomainPolicy.Hash(manifest);
                    var id = DomainPolicy.StableId("domain-definition:" + manifest.Key); var versionId = DomainPolicy.StableId("installed-domain-version:" + manifest.Key + ":" + hash);
                    var version = new DomainVersion(versionId, id, 1, 1, manifest, hash, "installed-code", now);
                    state.Versions.Add(versionId, version);
                    state.Definitions.Add(id, new(id, manifest.Key, DomainPolicy.WorkflowKey(manifest.Key), manifest.DisplayName, DomainStatus.Active, versionId, versionId, 1, true, now, now));
                    foreach (var required in manifest.Capabilities) state.Bindings.Add(new(versionId, required.Id, required.VersionConstraint, required.IsRequired));
                    var requestId = DomainPolicy.StableId("installed-domain-audit:" + versionId);
                    state.AuditEvents.Add(requestId, new(requestId, id, versionId, "installed", "installed-code", requestId, now, "{}"));
                }
                return currentEpoch;
            }, ct);
            Interlocked.Exchange(ref seededEpoch, applied);
        } finally { seeding.Release(); }
    }
    public async Task<DomainPreview> SaveDraftAsync(SaveDomainDraftRequest request, string actor, CancellationToken ct)
    {
        Guard.Require(request.RequestId != Guid.Empty && !string.IsNullOrWhiteSpace(actor) && actor.Length <= 120, 400, "A draft request ID and actor are required.");
        var manifest = DomainPolicy.Parse(request.Manifest); var hash = DomainPolicy.Hash(manifest);
        var fingerprint = PrivacyData.Hash(JsonSerializer.Serialize(new { request.ExpectedRevision, hash, actor }));
        await Seed(ct);
        return await store.UpdateAsync((state, _) => {
            if (state.AuditEvents.TryGetValue(request.RequestId, out var previous)) {
                using var details = JsonDocument.Parse(previous.DetailsJson);
                Guard.Require(previous.Action == "draft" && details.RootElement.GetProperty("fingerprint").GetString() == fingerprint, 409, "Draft request ID was reused.");
                return Preview(state, state.Definitions[previous.DomainId], state.Versions[previous.VersionId!.Value]);
            }
            var definition = state.Definitions.Values.SingleOrDefault(d => d.Key == manifest.Key);
            Guard.Require(definition is null ? request.ExpectedRevision is null : request.ExpectedRevision == definition.Revision, 409, "Domain changed; reload before creating another version.");
            Guard.Require(state.Definitions.Count < 100 || definition is not null, 409, "Domain capacity reached.");
            UniqueNames(state, manifest, definition?.Id);
            var now = Now(); var id = definition?.Id ?? DomainPolicy.StableId("domain-definition:" + manifest.Key);
            var versions = state.Versions.Values.Where(v => v.DomainId == id).ToArray();
            Guard.Require(versions.Length < 20, 409, "Domain version capacity reached.");
            var versionId = DomainPolicy.StableId("domain-draft-version:" + request.RequestId);
            var version = new DomainVersion(versionId, id, versions.Length == 0 ? 1 : versions.Max(v => v.Version) + 1, 1, manifest, hash, actor, now);
            state.Versions.Add(version.Id, version);
            foreach (var required in manifest.Capabilities) state.Bindings.Add(new(version.Id, required.Id, required.VersionConstraint, required.IsRequired));
            var status = DomainPolicy.Dependencies(manifest, capabilities).Count == 0 ? DomainStatus.PendingApproval : DomainStatus.Draft;
            definition = definition is null ? new(id, manifest.Key, DomainPolicy.WorkflowKey(manifest.Key), manifest.DisplayName, status, null, version.Id, 1, false, now, now) :
                definition with { LatestVersionId = version.Id, Revision = checked(definition.Revision + 1), UpdatedAt = now,
                    Status = definition.CurrentVersionId is null ? status : definition.Status };
            state.Definitions[id] = definition;
            state.AuditEvents.Add(request.RequestId, new(request.RequestId, id, version.Id, "draft", actor, request.RequestId, now, JsonSerializer.Serialize(new { fingerprint })));
            return Preview(state, definition, version);
        }, ct);
    }
    public async Task<IReadOnlyList<DomainPreview>> ListAsync(CancellationToken ct)
    {
        await Seed(ct); return await store.ReadAsync((state, _) => (IReadOnlyList<DomainPreview>)state.Definitions.Values.OrderBy(d => d.Key, StringComparer.Ordinal)
            .Select(d => Preview(state, d, state.Versions[d.LatestVersionId])).ToArray(), ct);
    }
    public async Task<DomainPreview> PreviewAsync(string key, Guid? versionId, CancellationToken ct)
    {
        await Seed(ct); return await store.ReadAsync((state, _) => {
            var definition = Definition(state, key);
            Guard.Require(state.Versions.TryGetValue(versionId ?? definition.LatestVersionId, out var version) && version.DomainId == definition.Id, 404, "Domain version not found.");
            return Preview(state, definition, version);
        }, ct);
    }
    public async Task<DomainOperationReceipt> ActivateAsync(string key, ActivateDomainRequest request, BudgetActor actor, CancellationToken ct)
    {
        DomainPolicy.RequireOwner(actor); Guard.Require(request.RequestId != Guid.Empty && request.ManifestHash is { Length: 64 } && request.Reason is { Length: <= 500 }, 400, "Activation confirmation is invalid.");
        var fingerprint = PrivacyData.Hash(JsonSerializer.Serialize(new { key, request, actor.SubjectId }));
        await Seed(ct);
        return await store.UpdateAsync((state, _) => {
            if (state.AuditEvents.TryGetValue(request.RequestId, out var previous)) return Replay(previous, fingerprint);
            var definition = Definition(state, key);
            Guard.Require(state.Versions.TryGetValue(request.VersionId, out var version) && definition.Revision == request.ExpectedRevision &&
                version.DomainId == definition.Id && version.ManifestHash == request.ManifestHash, 409, "Domain revision/version/hash changed.");
            UniqueNames(state, version.Manifest, definition.Id);
            var preview = Preview(state, definition, version); Guard.Require(preview.UnmetDependencies.Count == 0, 409, string.Join(" ", preview.UnmetDependencies));
            var visited = new HashSet<string>(StringComparer.Ordinal) { key }; var parent = version.Manifest.ParentKey;
            while (parent is not null) {
                Guard.Require(visited.Add(parent) && visited.Count <= 20, 409, "Domain parent cycle is forbidden.");
                var ancestor = Definition(state, parent); Guard.Require(ancestor.Status == DomainStatus.Active && ancestor.CurrentVersionId is not null, 409, "Parent domain must be active.");
                parent = state.Versions[ancestor.CurrentVersionId.Value].Manifest.ParentKey;
            }
            var now = Now(); var revision = checked(definition.Revision + 1);
            var action = definition.CurrentVersionId is Guid current && state.Versions[current].Version > version.Version ? "rollback" : "activate";
            state.Definitions[definition.Id] = definition with { CurrentVersionId = version.Id, Status = DomainStatus.Active, DisplayName = version.Manifest.DisplayName, Revision = revision, UpdatedAt = now };
            state.Approvals.Add(request.RequestId, new(request.RequestId, version.Id, actor.SubjectId, "Approved", DomainPolicy.Version, now, request.RequestId, fingerprint, revision, request.Reason));
            var receipt = new DomainOperationReceipt(request.RequestId, definition.Id, key, version.Id, revision, version.ManifestHash, now, action);
            state.AuditEvents.Add(request.RequestId, new(request.RequestId, definition.Id, version.Id, action, actor.SubjectId, request.RequestId, now, JsonSerializer.Serialize(new { fingerprint, receipt })));
            return receipt;
        }, ct);
    }
    private static DomainOperationReceipt Replay(DomainAuditEvent previous, string fingerprint)
    {
        using var details = JsonDocument.Parse(previous.DetailsJson);
        Guard.Require(details.RootElement.TryGetProperty("fingerprint", out var expected) && expected.GetString() == fingerprint &&
            details.RootElement.TryGetProperty("receipt", out _), 409, "Domain operation ID was reused.");
        return details.RootElement.GetProperty("receipt").Deserialize<DomainOperationReceipt>()!;
    }
    public async Task<DomainOperationReceipt> DisableAsync(string key, DisableDomainRequest request, BudgetActor actor, CancellationToken ct)
    {
        DomainPolicy.RequireOwner(actor); Guard.Require(request.RequestId != Guid.Empty && request.Reason is { Length: <= 500 }, 400, "Disable request is invalid.");
        var fingerprint = PrivacyData.Hash(JsonSerializer.Serialize(new { key, request, actor.SubjectId })); await Seed(ct);
        return await store.UpdateAsync((state, _) => {
            if (state.AuditEvents.TryGetValue(request.RequestId, out var previous)) return Replay(previous, fingerprint);
            var definition = Definition(state, key); Guard.Require(definition.Revision == request.ExpectedRevision && definition.CurrentVersionId is not null, 409, "Active domain revision changed.");
            var version = state.Versions[definition.CurrentVersionId.Value]; var now = Now(); var revision = checked(definition.Revision + 1);
            state.Definitions[definition.Id] = definition with { Status = DomainStatus.Disabled, Revision = revision, UpdatedAt = now };
            var receipt = new DomainOperationReceipt(request.RequestId, definition.Id, key, version.Id, revision, version.ManifestHash, now, "disable");
            state.AuditEvents.Add(request.RequestId, new(request.RequestId, definition.Id, version.Id, "disable", actor.SubjectId, request.RequestId, now, JsonSerializer.Serialize(new { fingerprint, receipt })));
            return receipt;
        }, ct);
    }
    private DomainResolved ValidateActive(DomainResolved resolved)
    {
        Guard.Require(resolved.Definition.Status == DomainStatus.Active && resolved.Definition.CurrentVersionId == resolved.Version.Id &&
            resolved.Version.DomainId == resolved.Definition.Id && resolved.Version.ManifestHash == DomainPolicy.Hash(resolved.Version.Manifest), 409, "Domain activation changed.");
        var installed = DomainCatalog.BuiltIns.FirstOrDefault(d => d.Key == resolved.Definition.Key);
        var trustedInstalled = installed is not null && resolved.Definition.BuiltIn &&
            resolved.Version.ManifestHash == DomainPolicy.Hash(DomainPolicy.BuiltIn(installed)) &&
            resolved.Version.Id == DomainPolicy.StableId("installed-domain-version:" + resolved.Definition.Key + ":" + resolved.Version.ManifestHash);
        Guard.Require(trustedInstalled || resolved.HasCurrentApproval, 409, "Domain requires approval under the current policy.");
        var gaps = DomainPolicy.Dependencies(resolved.Version.Manifest, capabilities);
        Guard.Require(gaps.Count == 0, 409, string.Join(" ", gaps)); return resolved;
    }
    public async Task<DomainResolved?> ResolveActiveAsync(string key, CancellationToken ct)
    {
        await Seed(ct); var resolved = (await store.ReadActiveAsync(key, ct)).SingleOrDefault(); if (resolved is null) return null;
        ValidateActive(resolved); var visited = new HashSet<string>(StringComparer.Ordinal) { key }; var parentKey = resolved.Version.Manifest.ParentKey;
        while (parentKey is not null) {
            Guard.Require(visited.Add(parentKey) && visited.Count <= 20, 409, "Domain parent cycle is forbidden.");
            var parent = (await store.ReadActiveAsync(parentKey, ct)).SingleOrDefault();
            Guard.Require(parent is not null && parent.Epoch == resolved.Epoch, 409, "Parent domain is unavailable or its privacy generation changed.");
            ValidateActive(parent); parentKey = parent.Version.Manifest.ParentKey;
        }
        return resolved;
    }
    public async Task<IReadOnlyList<DomainResolved>> ActiveAsync(CancellationToken ct)
    {
        await Seed(ct); var result = new List<DomainResolved>();
        var rows = (await store.ReadActiveAsync(null, ct)).ToDictionary(x => x.Definition.Key, StringComparer.Ordinal);
        foreach (var resolved in rows.Values) {
            try {
                ValidateActive(resolved); var visited = new HashSet<string>(StringComparer.Ordinal) { resolved.Definition.Key }; var parentKey = resolved.Version.Manifest.ParentKey;
                while (parentKey is not null) {
                    Guard.Require(visited.Add(parentKey) && visited.Count <= 20 && rows.ContainsKey(parentKey), 409, "Parent domain is unavailable.");
                    var parent = rows[parentKey]; ValidateActive(parent); parentKey = parent.Version.Manifest.ParentKey;
                }
                result.Add(resolved);
            } catch (DomainException) { /* Unavailable definitions remain visible in preview, not executable. */ }
        }
        return result;
    }
    public async Task<UserDomainState> GetStateAsync(string key, CancellationToken ct)
    {
        DomainExecutionContext.Require(key); await Seed(ct);
        return await store.ReadAsync((state, _) => {
            var definition = Definition(state, key);
            return state.UserStates.GetValueOrDefault(DomainPolicy.OwnerSubject + ":" + definition.Id) ??
                new(DomainPolicy.OwnerSubject, definition.Id, true, new(), null, 0, Now());
        }, ct);
    }
    public async Task<UserDomainState> SaveStateAsync(string key, SaveDomainStateRequest request, CancellationToken ct)
    {
        DomainExecutionContext.Require(key);
        Guard.Require(request.Preferences is not null && request.Preferences.Language is { Length: > 0 and <= 20 } && request.Preferences.DailyMinutes is > 0 and <= 240, 400, "Invalid domain preferences.");
        await Seed(ct);
        return await store.UpdateAsync((state, _) => {
            var definition = Definition(state, key); var id = DomainPolicy.OwnerSubject + ":" + definition.Id;
            var previous = state.UserStates.GetValueOrDefault(id); Guard.Require((previous?.Revision ?? 0) == request.ExpectedRevision, 409, "Domain preferences changed.");
            var updated = new UserDomainState(DomainPolicy.OwnerSubject, definition.Id, request.Enabled, request.Preferences, request.ProgressRef, checked(request.ExpectedRevision + 1), Now());
            state.UserStates[id] = updated; return updated;
        }, ct);
    }
}
