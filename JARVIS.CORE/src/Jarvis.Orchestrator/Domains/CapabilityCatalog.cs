using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Contracts;
using System.Text.Json;

namespace Jarvis.Orchestrator.Domains;

public enum CapabilityRisk { ReadOnly, CoreWrite, ExternalAction }
public sealed record CapabilityDescriptor(string Key, string Description, CapabilityRisk Risk, string Version = "1",
    string? InputSchemaJson = null, IReadOnlyList<ReasoningDataClass>? DataClasses = null,
    bool RequiresOwnerApproval = false, bool Irreversible = false, bool RequiresBudget = false, DeviceCapabilityRisk? ActionRisk = null);
public interface ICapabilityRegistry
{
    IReadOnlyList<CapabilityDescriptor> Installed { get; }
    CapabilityDescriptor Get(string key);
    bool Accepts(string key, string version, string arguments);
}
public interface ICapabilityCatalog : ICapabilityRegistry { }

// Only installed code registers capabilities. A domain definition cannot install one or alter its risk.
public sealed class CapabilityCatalog : ICapabilityCatalog
{
    private sealed record Entry(CapabilityDescriptor Descriptor, JsonElement? Schema);
    private readonly IReadOnlyDictionary<string, Entry> entries;
    public CapabilityCatalog(IEnumerable<CapabilityDescriptor> descriptors)
    {
        var entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var descriptor in descriptors) {
            Guard.Require(Regex.IsMatch(descriptor.Key, "^[a-z][a-z0-9_.-]{0,99}$") &&
                Regex.IsMatch(descriptor.Version, "^[1-9][0-9]*(?:\\.[0-9]+){0,2}$") &&
                !string.IsNullOrWhiteSpace(descriptor.Description) && descriptor.Description.Length <= 4000 && Enum.IsDefined(descriptor.Risk) &&
                (descriptor.DataClasses ?? []).All(Enum.IsDefined) && (descriptor.ActionRisk is null || Enum.IsDefined(descriptor.ActionRisk.Value)) &&
                (descriptor.ActionRisk is not (DeviceCapabilityRisk.Sensitive or DeviceCapabilityRisk.Irreversible) || descriptor.RequiresOwnerApproval) &&
                (!descriptor.Irreversible || descriptor.RequiresOwnerApproval && descriptor.Risk == CapabilityRisk.ExternalAction), 400, "Invalid capability descriptor.");
            var schema = descriptor.InputSchemaJson is null ? (JsonElement?)null : DeviceJsonSchema.ReadCapability(descriptor.InputSchemaJson);
            var frozen = descriptor with { DataClasses = Array.AsReadOnly((descriptor.DataClasses ?? []).Distinct().ToArray()) };
            Guard.Require(entries.TryAdd(descriptor.Key, new(frozen, schema)), 409, "Duplicate installed capability.");
        }
        this.entries = new ReadOnlyDictionary<string, Entry>(entries);
    }
    public IReadOnlyList<CapabilityDescriptor> Installed => entries.Values.Select(x => x.Descriptor).OrderBy(x => x.Key, StringComparer.Ordinal).ToArray();
    public CapabilityDescriptor Get(string key) => entries.TryGetValue(key, out var entry) ? entry.Descriptor : throw new DomainException(409, "Required capability is not installed.");
    public bool Accepts(string key, string version, string arguments) => entries.TryGetValue(key, out var entry) && entry.Descriptor.Version == version &&
        entry.Schema is { } schema && DeviceJsonSchema.TryReadObject(arguments, 400000, out var value) && DeviceJsonSchema.Accepts(schema, value);
}
