using System.Text.Json;
using System.Text.Json.Nodes;

namespace Jarvis.Orchestrator.Runtime;

// Deliberately limited to the closed schemas used by installed device capabilities.
internal static class DeviceJsonSchema
{
    // Voice metadata uses nullable types and descriptive annotations. Device grant schemas
    // keep their original strict grammar; execution shares the same closed-object validator.
    public static JsonElement ReadCapability(string json)
    {
        Guard.Require(TryReadObject(json, 16000, out _), 400, "Invalid capability schema.");
        var node = JsonNode.Parse(json)!.AsObject();
        Normalize(node);
        var schema = JsonSerializer.SerializeToElement(node);
        Guard.Require(IsSchema(schema, true) && schema.GetProperty("type").ValueEquals("object"), 400, "Unsupported capability schema.");
        return schema;
    }
    private static void Normalize(JsonObject schema)
    {
        schema.Remove("description"); schema.Remove("title"); schema.Remove("format");
        Guard.Require(!schema.ContainsKey("nullable"), 400, "Use standard nullable schema types.");
        if (schema["type"] is JsonArray types) {
            var names = types.Select(n => n?.GetValue<string>()).ToArray();
            Guard.Require(names.Length == 2 && names.Contains("null") && names.Distinct().Count() == 2, 400, "Unsupported nullable schema.");
            schema["type"] = names.Single(n => n != "null");
            schema["nullable"] = schema["enum"] is not JsonArray values || values.Any(n => n is null);
            if (schema["enum"] is JsonArray choices) for (var i = choices.Count - 1; i >= 0; i--) if (choices[i] is null) choices.RemoveAt(i);
        }
        if (schema["properties"] is JsonObject properties) foreach (var property in properties) Normalize(property.Value!.AsObject());
        if (schema["items"] is JsonObject items) Normalize(items);
    }
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

    private static bool IsSchema(JsonElement schema, bool nullable = false)
    {
        if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String) return false;
        if (schema.TryGetProperty("nullable", out var hint) && (!nullable || hint.ValueKind is not (JsonValueKind.True or JsonValueKind.False))) return false;

        return type.GetString() switch
        {
            "object" => IsObjectSchema(schema, nullable),
            "array" => HasOnly(schema, "type", "items", "minItems", "maxItems") &&
                schema.TryGetProperty("items", out var items) && IsSchema(items, nullable) &&
                TryBound(schema, "minItems", 0, out var minimum) &&
                TryBound(schema, "maxItems", int.MaxValue, out var maximum) && minimum <= maximum,
            "string" => HasOnly(schema, "type", "maxLength", "enum") &&
                TryBound(schema, "maxLength", int.MaxValue, out var length) &&
                (!schema.TryGetProperty("enum", out var values) ||
                    UniqueStrings(values, value => value.Length <= length) && values.GetArrayLength() > 0),
            "boolean" => HasOnly(schema, "type"),
            "number" or "integer" => HasOnly(schema, "type", "minimum", "maximum") &&
                TryNumericBound(schema, "minimum", decimal.MinValue, out var lower) &&
                TryNumericBound(schema, "maximum", decimal.MaxValue, out var upper) && lower <= upper,
            _ => false
        };
    }

    private static bool IsObjectSchema(JsonElement schema, bool nullable)
    {
        return HasOnly(schema, "type", "properties", "required", "additionalProperties") &&
            schema.TryGetProperty("additionalProperties", out var extra) && extra.ValueKind == JsonValueKind.False &&
            schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object &&
            properties.EnumerateObject().All(property => IsSchema(property.Value, nullable)) &&
            (!schema.TryGetProperty("required", out var required) ||
                UniqueStrings(required, name => properties.TryGetProperty(name, out _)));
    }

    private static bool HasOnly(JsonElement schema, params string[] keywords) =>
        schema.EnumerateObject().All(property => property.Name == "nullable" || keywords.Contains(property.Name, StringComparer.Ordinal));

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

    private static bool TryNumericBound(JsonElement schema, string name, decimal fallback, out decimal bound)
    {
        bound = fallback;
        return !schema.TryGetProperty(name, out var value) ||
            value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out bound);
    }

    private static bool AcceptsNumber(JsonElement schema, JsonElement value, bool integer) =>
        value.ValueKind == JsonValueKind.Number && (!integer || value.TryGetInt32(out _)) && value.TryGetDecimal(out var number) &&
        TryNumericBound(schema, "minimum", decimal.MinValue, out var min) && number >= min &&
        TryNumericBound(schema, "maximum", decimal.MaxValue, out var max) && number <= max;

    public static bool Accepts(JsonElement schema, JsonElement value) => value.ValueKind == JsonValueKind.Null
        ? schema.TryGetProperty("nullable", out var hint) && hint.ValueKind == JsonValueKind.True
        : schema.GetProperty("type").GetString() switch
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
        "number" => AcceptsNumber(schema, value, integer: false),
        "integer" => AcceptsNumber(schema, value, integer: true),
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
