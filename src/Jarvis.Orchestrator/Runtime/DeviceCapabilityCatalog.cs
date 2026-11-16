using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jarvis.Orchestrator.Runtime;

public enum DeviceCapabilityRisk { ReadOnly, Reversible, Sensitive, Irreversible }
public enum DeviceConfirmationPolicy { None, Owner }
public enum DeviceIdempotencyRule { CallerKey, SafeReplay, NeverReplay }

/// <summary>Installed, code-owned contract for one action a paired device may be granted.</summary>
public sealed record DeviceCapabilityDescriptor(
    string Id,
    string Version,
    string Name,
    string Description,
    DeviceCapabilityRisk Risk,
    DeviceConfirmationPolicy ConfirmationPolicy,
    DeviceIdempotencyRule IdempotencyRule,
    string ArgumentSchemaJson,
    string ReceiptSchemaJson);

public interface IDeviceCapabilityCatalog
{
    IReadOnlyList<DeviceCapabilityDescriptor> Installed { get; }
    DeviceCapabilityDescriptor Get(string id, string version);
}

/// <summary>Only descriptors supplied by application code exist. Database rows and device claims cannot install behavior.</summary>
public sealed class DeviceCapabilityCatalog : IDeviceCapabilityCatalog
{
    private static readonly Regex IdPattern = new("^[a-z][a-z0-9_.-]{0,99}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private readonly IReadOnlyDictionary<(string Id, string Version), DeviceCapabilityDescriptor> entries;

    public DeviceCapabilityCatalog(IEnumerable<DeviceCapabilityDescriptor> installed)
    {
        var entries = new Dictionary<(string Id, string Version), DeviceCapabilityDescriptor>();
        foreach (var descriptor in installed)
        {
            Guard.Require(IdPattern.IsMatch(descriptor.Id) && Regex.IsMatch(descriptor.Version, "^[1-9][0-9]*\\.[0-9]+\\.[0-9]+$"),
                400, "Device capability identity or version is invalid.");
            Guard.Require(!string.IsNullOrWhiteSpace(descriptor.Name) && descriptor.Name.Length <= 120 &&
                !string.IsNullOrWhiteSpace(descriptor.Description) && descriptor.Description.Length <= 2000 &&
                Enum.IsDefined(descriptor.Risk) && Enum.IsDefined(descriptor.ConfirmationPolicy) && Enum.IsDefined(descriptor.IdempotencyRule),
                400, "Device capability descriptor is invalid.");
            Guard.Require(descriptor.Risk is not (DeviceCapabilityRisk.Sensitive or DeviceCapabilityRisk.Irreversible) ||
                descriptor.ConfirmationPolicy == DeviceConfirmationPolicy.Owner, 400,
                "Sensitive and irreversible device capabilities require owner confirmation.");
            ValidateSchema(descriptor.ArgumentSchemaJson);
            ValidateSchema(descriptor.ReceiptSchemaJson);
            Guard.Require(entries.TryAdd((descriptor.Id, descriptor.Version), descriptor), 409, "Duplicate installed device capability version.");
        }
        this.entries = new ReadOnlyDictionary<(string Id, string Version), DeviceCapabilityDescriptor>(entries);
    }

    public IReadOnlyList<DeviceCapabilityDescriptor> Installed => entries.Values.OrderBy(x => x.Id, StringComparer.Ordinal)
        .ThenBy(x => x.Version, StringComparer.Ordinal).ToArray();

    public DeviceCapabilityDescriptor Get(string id, string version) => entries.TryGetValue((id, version), out var descriptor)
        ? descriptor : throw new DomainException(404, "Device capability version is not installed.");

    private static void ValidateSchema(string schema)
    {
        Guard.Require(schema is { Length: > 0 and <= 16_000 }, 400, "Device capability schema must be bounded JSON.");
        try
        {
            using var document = JsonDocument.Parse(schema);
            Guard.Require(document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "object",
                400, "Device capability schemas must describe a JSON object.");
        }
        catch (JsonException) { throw new DomainException(400, "Device capability schema must be valid JSON."); }
    }
}

public static class BuiltInDeviceCapabilities
{
    public static IReadOnlyList<DeviceCapabilityDescriptor> All { get; } =
    [
        new("homeassistant.turn", "1.0.0", "Control an approved light or switch", "Turn one allowlisted Home Assistant entity on or off.",
            DeviceCapabilityRisk.Reversible, DeviceConfirmationPolicy.Owner, DeviceIdempotencyRule.CallerKey,
            """{"type":"object","properties":{"entityId":{"type":"string","maxLength":120},"action":{"type":"string","enum":["turn_on","turn_off"]}},"required":["entityId","action"],"additionalProperties":false}""",
            """{"type":"object","properties":{"accepted":{"type":"boolean"},"detail":{"type":"string","maxLength":1000}},"required":["accepted"],"additionalProperties":false}""")
    ];
}
