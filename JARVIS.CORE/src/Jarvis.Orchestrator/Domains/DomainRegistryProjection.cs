using Jarvis.Contracts;

namespace Jarvis.Orchestrator.Domains;

public static class DomainRegistryProjection
{
    public static DomainResolved? Active(DomainRegistryState state, string key)
    {
        var definition = state.Definitions.Values.SingleOrDefault(d => d.Key == key && d.Status == DomainStatus.Active && d.CurrentVersionId is not null);
        if (definition is null || state.UserStates.GetValueOrDefault(DomainPolicy.OwnerSubject + ":" + definition.Id)?.Enabled == false) return null;
        if (!state.Versions.TryGetValue(definition.CurrentVersionId!.Value, out var version)) return null;
        var visited = new HashSet<string>(StringComparer.Ordinal) { key }; var parent = version.Manifest.ParentKey;
        while (parent is not null) {
            if (!visited.Add(parent) || visited.Count > 20) return null;
            var ancestor = state.Definitions.Values.SingleOrDefault(d => d.Key == parent && d.Status == DomainStatus.Active && d.CurrentVersionId is not null);
            if (ancestor is null || state.UserStates.GetValueOrDefault(DomainPolicy.OwnerSubject + ":" + ancestor.Id)?.Enabled == false ||
                !state.Versions.TryGetValue(ancestor.CurrentVersionId!.Value, out var ancestorVersion)) return null;
            parent = ancestorVersion.Manifest.ParentKey;
        }
        var approval = state.Approvals.Values.Where(a => a.DomainVersionId == definition.CurrentVersionId && a.PolicyVersion == DomainPolicy.Version && a.Decision == "Approved").OrderByDescending(a => a.At).FirstOrDefault();
        return new(definition, version, 0, approval is not null, approval?.RiskClass);
    }
}
