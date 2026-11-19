using System.Text.Json;
using Jarvis.Contracts;

namespace Jarvis.Orchestrator.Runtime;

public static class WindowsDeviceCapabilities
{
    private const string Ids = """{"type":"object","properties":{"allowedIds":{"type":"array","items":{"type":"string","maxLength":40},"minItems":1,"maxItems":50}},"required":["allowedIds"],"additionalProperties":false}""";
    private const string Allowed = """{"type":"object","properties":{"allowed":{"type":"boolean"}},"required":["allowed"],"additionalProperties":false}""";
    private const string Actions = """{"type":"object","properties":{"allowedActions":{"type":"array","items":{"type":"string","maxLength":10},"minItems":1,"maxItems":3}},"required":["allowedActions"],"additionalProperties":false}""";
    private const string Volume = """{"type":"object","properties":{"allowedActions":{"type":"array","items":{"type":"string","maxLength":10},"minItems":1,"maxItems":3},"maximumSteps":{"type":"integer","minimum":1,"maximum":10}},"required":["allowedActions","maximumSteps"],"additionalProperties":false}""";
    private const string Receipt = """{"type":"object","properties":{"accepted":{"type":"boolean"},"detail":{"type":"string","maxLength":1000},"names":{"type":"array","items":{"type":"string","maxLength":120},"maxItems":20}},"required":["accepted"],"additionalProperties":false}""";
    public static IReadOnlyList<DeviceCapabilityDescriptor> All { get; } = WindowsCommandProtocol.Schemas.Select(p => new DeviceCapabilityDescriptor(p.Key, WindowsCommandProtocol.Version,
        p.Key, "Execute only this installed, bounded Windows action; local owner allow-lists also apply.", p.Key == WindowsCommandProtocol.Files ? DeviceCapabilityRisk.ReadOnly : DeviceCapabilityRisk.Reversible,
        DeviceConfirmationPolicy.None, DeviceIdempotencyRule.NeverReplay, p.Value, Receipt,
        p.Key switch { WindowsCommandProtocol.App or WindowsCommandProtocol.Url or WindowsCommandProtocol.Files => Ids,
            WindowsCommandProtocol.Media => Actions, WindowsCommandProtocol.Volume => Volume, _ => Allowed })).ToArray();
    public static bool Installed(string id, string version) => version == WindowsCommandProtocol.Version && WindowsCommandProtocol.Schemas.ContainsKey(id);
    public static bool Constraints(string id, string version, string? json)
    {
        if (!Installed(id, version) || !DeviceJsonSchema.TryReadObject(json, 8000, out var value) || !DeviceJsonSchema.Accepts(DeviceJsonSchema.Read(All.Single(c => c.Id == id).ConstraintSchemaJson), value)) return false;
        if (value.TryGetProperty("allowedIds", out var ids)) return ids.EnumerateArray().All(i => WindowsCommandProtocol.IsId(i.GetString())) && ids.EnumerateArray().Select(i => i.GetString()).Distinct().Count() == ids.GetArrayLength();
        if (value.TryGetProperty("allowedActions", out var actions)) return actions.EnumerateArray().All(a => id == WindowsCommandProtocol.Media ? a.GetString() is "play" or "pause" or "next" : a.GetString() is "up" or "down" or "mute") && actions.EnumerateArray().Select(a => a.GetString()).Distinct().Count() == actions.GetArrayLength();
        return value.GetProperty("allowed").ValueKind == JsonValueKind.True;
    }
    public static bool Permits(string id, string version, string? constraints, string? arguments)
    {
        if (!Constraints(id, version, constraints) || !DeviceJsonSchema.TryReadObject(arguments, 8000, out _)) return false;
        try {
            var parsed = WindowsCommandProtocol.Parse(id, arguments!); using var document = JsonDocument.Parse(constraints!); var root = document.RootElement;
            if (parsed.TargetId is { } target) return root.GetProperty("allowedIds").EnumerateArray().Any(i => i.ValueEquals(target));
            if (parsed.Action is { } action) return root.GetProperty("allowedActions").EnumerateArray().Any(i => i.ValueEquals(action)) && (id != WindowsCommandProtocol.Volume || parsed.Steps <= root.GetProperty("maximumSteps").GetInt32());
            return root.GetProperty("allowed").GetBoolean();
        } catch (Exception e) when (e is ArgumentException or InvalidOperationException or JsonException or KeyNotFoundException) { return false; }
    }
}
