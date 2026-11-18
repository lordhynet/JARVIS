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
        WindowsDeviceCapabilities.Installed(id, version)
            ? entries.TryGetValue((id, version), out var windows) && DeviceJsonSchema.TryReadObject(constraintsJson, 8000, out var scope) &&
                DeviceJsonSchema.Accepts(windows.Constraints, scope) && WindowsDeviceCapabilities.Constraints(id, version, constraintsJson)
            : TryReadAllowlist(id, version, constraintsJson, out _, out _, out _);

    public bool AcceptsReceipt(string id, string version, DeviceReceiptKind kind, string? payloadJson) =>
        entries.TryGetValue((id, version), out var entry) &&
        DeviceJsonSchema.TryReadObject(payloadJson, 8_000, out var payload) && DeviceJsonSchema.Accepts(entry.Receipt, payload) &&
        (!(BuiltInDeviceCapabilities.IsHomeAssistant(id, version) || WindowsDeviceCapabilities.Installed(id, version)) ||
            payload.TryGetProperty("accepted", out var accepted) &&
            accepted.ValueKind == (kind == DeviceReceiptKind.Failed ? JsonValueKind.False : JsonValueKind.True));

    public bool Permits(string id, string version, string? constraintsJson, string? argumentsJson) =>
        WindowsDeviceCapabilities.Installed(id, version) ? entries.TryGetValue((id, version), out var windows) &&
            DeviceJsonSchema.TryReadObject(argumentsJson, 8000, out var parameters) && DeviceJsonSchema.Accepts(windows.Arguments, parameters) &&
            HasValidConstraints(id, version, constraintsJson) && WindowsDeviceCapabilities.Permits(id, version, constraintsJson, argumentsJson) :
        TryReadAllowlist(id, version, constraintsJson, out var entry, out var allowed, out var constraints) &&
        DeviceJsonSchema.TryReadObject(argumentsJson, 8_000, out var arguments) &&
        DeviceJsonSchema.Accepts(entry!.Arguments, arguments) &&
        arguments.TryGetProperty("entityId", out var entity) && entity.ValueKind == JsonValueKind.String &&
        allowed.Contains(entity.GetString()!) && WithinConstraints(id, arguments, constraints);

    private static bool WithinConstraints(string id, JsonElement arguments, JsonElement constraints)
    {
        if (BuiltInDeviceCapabilities.IsSensitiveHomeAssistant(id))
        {
            var action = arguments.GetProperty("action").GetString()!;
            return constraints.GetProperty("allowedActions").EnumerateArray().Any(x => x.ValueEquals(action)) &&
                (id != BuiltInDeviceCapabilities.HomeAssistantCoverSet ||
                    arguments.TryGetProperty("position", out _) == (action == "set_cover_position"));
        }
        if (id == BuiltInDeviceCapabilities.HomeAssistantClimateSet)
        {
            var temperature = arguments.GetProperty("temperatureC").GetDecimal();
            return temperature >= constraints.GetProperty("minC").GetDecimal() &&
                temperature <= constraints.GetProperty("maxC").GetDecimal();
        }
        if (id != BuiltInDeviceCapabilities.HomeAssistantLightSet) return true;
        var hasBrightness = arguments.TryGetProperty("brightnessPct", out var brightness);
        var hasKelvin = arguments.TryGetProperty("colorTempKelvin", out _);
        var hasRgb = arguments.TryGetProperty("rgbColor", out _);
        return (hasBrightness || hasKelvin || hasRgb) && !(hasKelvin && hasRgb) &&
            (!(hasKelvin || hasRgb) || !hasBrightness || brightness.GetInt32() > 0);
    }

    private bool TryReadAllowlist(string id, string version, string? constraintsJson, out Entry? entry, out HashSet<string> allowed,
        out JsonElement constraints)
    {
        var entityIds = new HashSet<string>(StringComparer.Ordinal);
        allowed = entityIds;
        constraints = default;
        // A schema alone cannot define how an owner constraint governs executable arguments.
        return entries.TryGetValue((id, version), out entry) &&
            BuiltInDeviceCapabilities.IsHomeAssistant(id, version) &&
            DeviceJsonSchema.TryReadObject(constraintsJson, 8_000, out constraints) &&
            DeviceJsonSchema.Accepts(entry.Constraints, constraints) &&
            constraints.TryGetProperty("allowedEntityIds", out var entities) && entities.ValueKind == JsonValueKind.Array &&
            entities.EnumerateArray().All(entity => entity.ValueKind == JsonValueKind.String && entity.GetString() is { } entityId &&
                BuiltInDeviceCapabilities.IsEntityFor(id, entityId) && entityIds.Add(entityId)) && entityIds.Count > 0 &&
            (id != BuiltInDeviceCapabilities.HomeAssistantClimateSet ||
                constraints.GetProperty("minC").GetDecimal() <= constraints.GetProperty("maxC").GetDecimal()) &&
            (!BuiltInDeviceCapabilities.IsSensitiveHomeAssistant(id) || HasUniqueActions(constraints));
    }

    private static bool HasUniqueActions(JsonElement constraints)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        return constraints.GetProperty("allowedActions").EnumerateArray().All(x => names.Add(x.GetString()!));
    }
}

public static class BuiltInDeviceCapabilities
{
    public const string HomeAssistantTurn = "homeassistant.turn";
    public const string HomeAssistantTurnVersion = "1.0.0";
    public const string HomeAssistantLightSet = "homeassistant.light.set";
    public const string HomeAssistantSceneActivate = "homeassistant.scene.activate";
    public const string HomeAssistantClimateSet = "homeassistant.climate.set";
    public const string HomeAssistantCoverSet = "homeassistant.cover.set";
    public const string HomeAssistantLockSet = "homeassistant.lock.set";
    public const string HomeAssistantAlarmSet = "homeassistant.alarm.set";

    private const string Allowlist = """{"type":"object","properties":{"allowedEntityIds":{"type":"array","items":{"type":"string","maxLength":120},"minItems":1,"maxItems":50}},"required":["allowedEntityIds"],"additionalProperties":false}""";
    private const string Receipt = """{"type":"object","properties":{"accepted":{"type":"boolean"},"detail":{"type":"string","maxLength":1000}},"required":["accepted"],"additionalProperties":false}""";
    private const string CoverActions = """["open_cover","close_cover","set_cover_position"]""";
    private const string LockActions = """["lock","unlock"]""";
    private const string AlarmActions = """["alarm_arm_home","alarm_arm_away","alarm_arm_night","alarm_disarm"]""";

    public static bool IsSensitiveHomeAssistant(string id) => id is HomeAssistantCoverSet or HomeAssistantLockSet or HomeAssistantAlarmSet;

    public static bool IsHomeAssistant(string id, string version) => version == HomeAssistantTurnVersion &&
        (id is HomeAssistantTurn or HomeAssistantLightSet or HomeAssistantSceneActivate or HomeAssistantClimateSet || IsSensitiveHomeAssistant(id));

    private static readonly Regex Entity = new(@"\A(light|switch|scene|climate|cover|lock|alarm_control_panel)\.[a-z0-9_]{1,100}\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    internal static bool IsEntityFor(string capability, string entity) => Entity.IsMatch(entity) && (capability switch
    {
        HomeAssistantTurn => entity.StartsWith("light.", StringComparison.Ordinal) || entity.StartsWith("switch.", StringComparison.Ordinal),
        HomeAssistantLightSet => entity.StartsWith("light.", StringComparison.Ordinal),
        HomeAssistantSceneActivate => entity.StartsWith("scene.", StringComparison.Ordinal),
        HomeAssistantClimateSet => entity.StartsWith("climate.", StringComparison.Ordinal),
        HomeAssistantCoverSet => entity.StartsWith("cover.", StringComparison.Ordinal),
        HomeAssistantLockSet => entity.StartsWith("lock.", StringComparison.Ordinal),
        HomeAssistantAlarmSet => entity.StartsWith("alarm_control_panel.", StringComparison.Ordinal),
        _ => false
    });

    public static IReadOnlyList<DeviceCapabilityDescriptor> All { get; } =
    [
        new(HomeAssistantTurn, HomeAssistantTurnVersion, "Control an approved light or switch", "Turn one allowlisted Home Assistant entity on or off.",
            DeviceCapabilityRisk.Reversible, DeviceConfirmationPolicy.Owner, DeviceIdempotencyRule.CallerKey,
            """{"type":"object","properties":{"entityId":{"type":"string","maxLength":120},"action":{"type":"string","enum":["turn_on","turn_off"]}},"required":["entityId","action"],"additionalProperties":false}""",
            Receipt, Allowlist),
        new(HomeAssistantLightSet, HomeAssistantTurnVersion, "Set light brightness or colour", "Set the brightness, RGB colour or colour temperature of an allowlisted light.",
            DeviceCapabilityRisk.Reversible, DeviceConfirmationPolicy.Owner, DeviceIdempotencyRule.CallerKey,
            """{"type":"object","properties":{"entityId":{"type":"string","maxLength":120},"brightnessPct":{"type":"integer","minimum":0,"maximum":100},"colorTempKelvin":{"type":"integer","minimum":1000,"maximum":10000},"rgbColor":{"type":"array","items":{"type":"integer","minimum":0,"maximum":255},"minItems":3,"maxItems":3}},"required":["entityId"],"additionalProperties":false}""",
            Receipt, Allowlist),
        new(HomeAssistantSceneActivate, HomeAssistantTurnVersion, "Activate an approved scene", "Activate a named scene that the owner has explicitly granted.",
            DeviceCapabilityRisk.Reversible, DeviceConfirmationPolicy.Owner, DeviceIdempotencyRule.NeverReplay,
            """{"type":"object","properties":{"entityId":{"type":"string","maxLength":120}},"required":["entityId"],"additionalProperties":false}""",
            Receipt, Allowlist),
        new(HomeAssistantClimateSet, HomeAssistantTurnVersion, "Set an approved target temperature", "Set a climate entity's target temperature inside an owner-defined Celsius range.",
            DeviceCapabilityRisk.Reversible, DeviceConfirmationPolicy.Owner, DeviceIdempotencyRule.CallerKey,
            """{"type":"object","properties":{"entityId":{"type":"string","maxLength":120},"temperatureC":{"type":"number","minimum":-273.15,"maximum":1000}},"required":["entityId","temperatureC"],"additionalProperties":false}""",
            Receipt,
            """{"type":"object","properties":{"allowedEntityIds":{"type":"array","items":{"type":"string","maxLength":120},"minItems":1,"maxItems":50},"minC":{"type":"number","minimum":-273.15,"maximum":1000},"maxC":{"type":"number","minimum":-273.15,"maximum":1000}},"required":["allowedEntityIds","minC","maxC"],"additionalProperties":false}"""),
        Sensitive(HomeAssistantCoverSet, "Control an approved cover", "Open, close or position a cover after owner confirmation.", CoverActions,
            ""","position":{"type":"integer","minimum":0,"maximum":100}"""),
        Sensitive(HomeAssistantLockSet, "Control an approved lock", "Lock or unlock a granted lock after owner confirmation.", LockActions),
        Sensitive(HomeAssistantAlarmSet, "Control an approved alarm", "Arm or disarm a granted alarm panel after owner confirmation.", AlarmActions)
    ];

    private static DeviceCapabilityDescriptor Sensitive(string id, string name, string description, string actions, string extra = "") =>
        new(id, HomeAssistantTurnVersion, name, description, DeviceCapabilityRisk.Sensitive, DeviceConfirmationPolicy.Owner, DeviceIdempotencyRule.NeverReplay,
            $$$"""{"type":"object","properties":{"entityId":{"type":"string","maxLength":120},"action":{"type":"string","enum":{{{actions}}}}{{{extra}}}},"required":["entityId","action"],"additionalProperties":false}""",
            Receipt,
            $$$"""{"type":"object","properties":{"allowedEntityIds":{"type":"array","items":{"type":"string","maxLength":120},"minItems":1,"maxItems":50},"allowedActions":{"type":"array","items":{"type":"string","enum":{{{actions}}}},"minItems":1,"maxItems":4}},"required":["allowedEntityIds","allowedActions"],"additionalProperties":false}""");
}
