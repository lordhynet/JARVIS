using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Domains;

public enum CapabilityRisk { ReadOnly, CoreWrite, ExternalAction }
public sealed record CapabilityDescriptor(string Key, string Description, CapabilityRisk Risk);
public interface ICapabilityCatalog
{
    IReadOnlyList<CapabilityDescriptor> Installed { get; }
    CapabilityDescriptor Get(string key);
}

// Only installed code registers capabilities. A domain definition cannot install one or alter its risk.
public sealed class CapabilityCatalog(IEnumerable<CapabilityDescriptor> installed) : ICapabilityCatalog
{
    private readonly IReadOnlyDictionary<string, CapabilityDescriptor> entries = Build(installed);
    private static IReadOnlyDictionary<string, CapabilityDescriptor> Build(IEnumerable<CapabilityDescriptor> descriptors)
    {
        var entries = new Dictionary<string, CapabilityDescriptor>(StringComparer.Ordinal);
        foreach (var descriptor in descriptors) {
            Guard.Require(Regex.IsMatch(descriptor.Key, "^[a-z][a-z0-9_.-]{0,99}$") &&
                !string.IsNullOrWhiteSpace(descriptor.Description) && descriptor.Description.Length <= 4000 && Enum.IsDefined(descriptor.Risk), 400, "Invalid capability descriptor.");
            Guard.Require(entries.TryAdd(descriptor.Key, descriptor), 409, "Duplicate installed capability.");
        }
        return new ReadOnlyDictionary<string, CapabilityDescriptor>(entries);
    }
    public IReadOnlyList<CapabilityDescriptor> Installed => entries.Values.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray();
    public CapabilityDescriptor Get(string key) => entries.TryGetValue(key, out var descriptor) ? descriptor : throw new DomainException(409, "Required capability is not installed.");
}
