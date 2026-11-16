using System.Text.Json;

namespace Jarvis.Orchestrator.Runtime;

// Deliberately limited to the closed schemas used by installed device capabilities.
internal static class DeviceJsonSchema
{
    public static JsonElement Read(string json)
    {
        Guard.Require(TryReadObject(json, 16_000, out var schema) && IsSchema(schema) && schema.GetProperty("type").GetString() == "object",
            400, "Device capability schema must be a supported, closed JSON object schema.");
        return schema;
    }

    public static bool TryReadObject(string? json, int maxLength, out JsonElement value)
    {
        value = default;
        if (json is not { Length: > 0 } || json.Length > maxLength) return false;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 12 });
            if (!ValidJson(document.RootElement) || document.RootElement.ValueKind != JsonValueKind.Object) return false;
            value = document.RootElement.Clone();
            return true;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool ValidJson(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Object => ObjectNamesAreUnique(value) &&
            value.EnumerateObject().All(property => ValidJson(property.Value)),
        JsonValueKind.Array => value.EnumerateArray().All(ValidJson),
        JsonValueKind.String => value.GetString() is not null,
        _ => true
    };

    private static bool ObjectNamesAreUnique(JsonElement value)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        return value.EnumerateObject().All(property => names.Add(property.Name));
    }

    private static bool IsSchema(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String) return false;

        return type.GetString() switch
        {
            "object" => IsObjectSchema(schema),
            "array" => HasOnly(schema, "type", "items", "minItems", "maxItems") &&
                schema.TryGetProperty("items", out var items) && IsSchema(items) &&
                TryBound(schema, "minItems", 0, out var minimum) &&
                TryBound(schema, "maxItems", int.MaxValue, out var maximum) && minimum <= maximum,
            "string" => HasOnly(schema, "type", "maxLength", "enum") &&
                TryBound(schema, "maxLength", int.MaxValue, out var length) &&
                (!schema.TryGetProperty("enum", out var values) ||
                    UniqueStrings(values, value => value.Length <= length) && values.GetArrayLength() > 0),
            "boolean" => HasOnly(schema, "type"),
            _ => false
        };
    }

    private static bool IsObjectSchema(JsonElement schema)
    {
        return HasOnly(schema, "type", "properties", "required", "additionalProperties") &&
            schema.TryGetProperty("additionalProperties", out var extra) && extra.ValueKind == JsonValueKind.False &&
            schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object &&
            properties.EnumerateObject().All(property => IsSchema(property.Value)) &&
            (!schema.TryGetProperty("required", out var required) ||
                UniqueStrings(required, name => properties.TryGetProperty(name, out _)));
    }

    private static bool HasOnly(JsonElement schema, params string[] keywords) =>
        schema.EnumerateObject().All(property => keywords.Contains(property.Name, StringComparer.Ordinal));

    private static bool UniqueStrings(JsonElement items, Func<string, bool> accepts)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return items.ValueKind == JsonValueKind.Array &&
            items.EnumerateArray().All(item => item.ValueKind == JsonValueKind.String &&
                item.GetString() is { } text && seen.Add(text) && accepts(text));
    }

    private static bool TryBound(JsonElement schema, string name, int fallback, out int bound)
    {
        bound = fallback;
        return !schema.TryGetProperty(name, out var value) ||
            value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out bound) && bound >= 0;
    }

    private static int Bound(JsonElement schema, string name, int fallback) =>
        schema.TryGetProperty(name, out var value) ? value.GetInt32() : fallback;

    public static bool Accepts(JsonElement schema, JsonElement value) => schema.GetProperty("type").GetString() switch
    {
        "object" => AcceptsObject(schema, value),
        "array" => value.ValueKind == JsonValueKind.Array &&
            value.GetArrayLength() >= Bound(schema, "minItems", 0) &&
            value.GetArrayLength() <= Bound(schema, "maxItems", int.MaxValue) &&
            value.EnumerateArray().All(item => Accepts(schema.GetProperty("items"), item)),
        "string" => value.ValueKind == JsonValueKind.String && value.GetString() is { } text &&
            text.Length <= Bound(schema, "maxLength", int.MaxValue) &&
            (!schema.TryGetProperty("enum", out var allowed) || allowed.EnumerateArray().Any(item => item.ValueEquals(text))),
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        _ => false
    };

    private static bool AcceptsObject(JsonElement schema, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var properties = schema.GetProperty("properties");
        return (!schema.TryGetProperty("required", out var required) ||
                required.EnumerateArray().All(name => value.TryGetProperty(name.GetString()!, out _))) &&
            value.EnumerateObject().All(property => properties.TryGetProperty(property.Name, out var child) &&
                Accepts(child, property.Value));
    }
}
