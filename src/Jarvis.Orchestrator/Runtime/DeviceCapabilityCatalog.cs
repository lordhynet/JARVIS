using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Jarvis.Contracts;

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
    string ReceiptSchemaJson,
    string ConstraintSchemaJson);

public interface IDeviceCapabilityCatalog
{
    IReadOnlyList<DeviceCapabilityDescriptor> Installed { get; }
    DeviceCapabilityDescriptor Get(string id, string version);
    bool HasValidConstraints(string id, string version, string? constraintsJson);
    bool Permits(string id, string version, string? constraintsJson, string? argumentsJson);
    bool AcceptsReceipt(string id, string version, DeviceReceiptKind kind, string? payloadJson);
}

/// <summary>Only descriptors supplied by application code exist. Database rows and device claims cannot install behavior.</summary>
public sealed class DeviceCapabilityCatalog : IDeviceCapabilityCatalog
{
    private static readonly Regex IdPattern = new("^[a-z][a-z0-9_.-]{0,99}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private sealed record Entry(DeviceCapabilityDescriptor Descriptor, JsonElement Arguments, JsonElement Constraints, JsonElement Receipt);
    private readonly IReadOnlyDictionary<(string Id, string Version), Entry> entries;

    public DeviceCapabilityCatalog(IEnumerable<DeviceCapabilityDescriptor> installed)
    {
        var entries = new Dictionary<(string Id, string Version), Entry>();
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
            var arguments = DeviceJsonSchema.Read(descriptor.ArgumentSchemaJson);
            var constraints = DeviceJsonSchema.Read(descriptor.ConstraintSchemaJson);
            var receipt = DeviceJsonSchema.Read(descriptor.ReceiptSchemaJson);
            Guard.Require(entries.TryAdd((descriptor.Id, descriptor.Version), new(descriptor, arguments, constraints, receipt)),
                409, "Duplicate installed device capability version.");
        }
        this.entries = new ReadOnlyDictionary<(string Id, string Version), Entry>(entries);
    }

    public IReadOnlyList<DeviceCapabilityDescriptor> Installed => entries.Values.Select(x => x.Descriptor).OrderBy(x => x.Id, StringComparer.Ordinal)
        .ThenBy(x => x.Version, StringComparer.Ordinal).ToArray();

    public DeviceCapabilityDescriptor Get(string id, string version) => entries.TryGetValue((id, version), out var descriptor)
        ? descriptor.Descriptor : throw new DomainException(404, "Device capability version is not installed.");

    public bool HasValidConstraints(string id, string version, string? constraintsJson) =>
        TryReadAllowlist(id, version, constraintsJson, out _, out _);

    public bool AcceptsReceipt(string id, string version, DeviceReceiptKind kind, string? payloadJson) =>
        entries.TryGetValue((id, version), out var entry) &&
        DeviceJsonSchema.TryReadObject(payloadJson, 8_000, out var payload) && DeviceJsonSchema.Accepts(entry.Receipt, payload) &&
        (id != BuiltInDeviceCapabilities.HomeAssistantTurn || version != BuiltInDeviceCapabilities.HomeAssistantTurnVersion ||
            payload.TryGetProperty("accepted", out var accepted) &&
            accepted.ValueKind == (kind == DeviceReceiptKind.Failed ? JsonValueKind.False : JsonValueKind.True));

    public bool Permits(string id, string version, string? constraintsJson, string? argumentsJson) =>
        TryReadAllowlist(id, version, constraintsJson, out var entry, out var allowed) &&
        DeviceJsonSchema.TryReadObject(argumentsJson, 8_000, out var arguments) &&
        DeviceJsonSchema.Accepts(entry!.Arguments, arguments) &&
        arguments.TryGetProperty("entityId", out var entity) && entity.ValueKind == JsonValueKind.String &&
        allowed.Contains(entity.GetString()!);

    private bool TryReadAllowlist(string id, string version, string? constraintsJson, out Entry? entry, out HashSet<string> allowed)
    {
        var entityIds = new HashSet<string>(StringComparer.Ordinal);
        allowed = entityIds;
        // A schema alone cannot define how an owner constraint governs executable arguments.
        return entries.TryGetValue((id, version), out entry) &&
            id == BuiltInDeviceCapabilities.HomeAssistantTurn && version == BuiltInDeviceCapabilities.HomeAssistantTurnVersion &&
            DeviceJsonSchema.TryReadObject(constraintsJson, 8_000, out var constraints) &&
            DeviceJsonSchema.Accepts(entry.Constraints, constraints) &&
            constraints.TryGetProperty("allowedEntityIds", out var entities) && entities.ValueKind == JsonValueKind.Array &&
            entities.EnumerateArray().All(entity => entity.ValueKind == JsonValueKind.String && entity.GetString() is { } entityId &&
                BuiltInDeviceCapabilities.IsHomeAssistantEntityId(entityId) && entityIds.Add(entityId)) && entityIds.Count > 0;
    }
}

public static class BuiltInDeviceCapabilities
{
    public const string HomeAssistantTurn = "homeassistant.turn";
    public const string HomeAssistantTurnVersion = "1.0.0";
    private static readonly Regex HomeAssistantEntity = new(@"\A(light|switch)\.[a-z0-9_]{1,100}\z", RegexOptions.CultureInvariant);
    internal static bool IsHomeAssistantEntityId(string? id) => id is not null && HomeAssistantEntity.IsMatch(id);

    public static IReadOnlyList<DeviceCapabilityDescriptor> All { get; } =
    [
        new(HomeAssistantTurn, HomeAssistantTurnVersion, "Control an approved light or switch", "Turn one allowlisted Home Assistant entity on or off.",
            DeviceCapabilityRisk.Reversible, DeviceConfirmationPolicy.Owner, DeviceIdempotencyRule.CallerKey,
            """{"type":"object","properties":{"entityId":{"type":"string","maxLength":120},"action":{"type":"string","enum":["turn_on","turn_off"]}},"required":["entityId","action"],"additionalProperties":false}""",
            """{"type":"object","properties":{"accepted":{"type":"boolean"},"detail":{"type":"string","maxLength":1000}},"required":["accepted"],"additionalProperties":false}""",
            """{"type":"object","properties":{"allowedEntityIds":{"type":"array","items":{"type":"string","maxLength":120},"minItems":1,"maxItems":50}},"required":["allowedEntityIds"],"additionalProperties":false}""")
    ];
}
