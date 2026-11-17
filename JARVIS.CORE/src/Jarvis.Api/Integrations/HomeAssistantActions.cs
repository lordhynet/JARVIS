using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Api.Integrations;

public sealed partial class ProviderGateway
{
    internal async Task<string> ExecuteVerifiedHomeCommandAsync(DeliveredDeviceCommand delivery,
        Func<CancellationToken, Task> authorizeDispatch, TimeProvider clock, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(delivery.ArgumentsJson);
        var arguments = json.RootElement;
        var entity = arguments.GetProperty("entityId").GetString()!;
        switch (delivery.CapabilityId)
        {
            case BuiltInDeviceCapabilities.HomeAssistantTurn:
                var action = arguments.GetProperty("action").GetString()!;
                await authorizeDispatch(ct);
                await ExecuteAsync(entity, action, ct);
                return await VerifyHomeStateAsync(entity, action, ct);
            case BuiltInDeviceCapabilities.HomeAssistantLightSet:
                return await SetLightAsync(entity, arguments, authorizeDispatch, ct);
            case BuiltInDeviceCapabilities.HomeAssistantSceneActivate:
                return await ActivateSceneAsync(entity, authorizeDispatch, ct);
            case BuiltInDeviceCapabilities.HomeAssistantClimateSet:
                return await SetClimateAsync(entity, arguments.GetProperty("temperatureC").GetDecimal(), authorizeDispatch, ct);
            case BuiltInDeviceCapabilities.HomeAssistantCoverSet:
            case BuiltInDeviceCapabilities.HomeAssistantLockSet:
            case BuiltInDeviceCapabilities.HomeAssistantAlarmSet:
                return await SetSensitiveEntityAsync(entity, delivery, arguments, authorizeDispatch, clock, ct);
            default:
                throw new DomainException(409, "The Home Assistant executor does not support this capability.");
        }
    }

    private async Task<string> SetSensitiveEntityAsync(string entity, DeliveredDeviceCommand delivery, JsonElement arguments,
        Func<CancellationToken, Task> authorizeDispatch, TimeProvider clock, CancellationToken ct)
    {
        var action = arguments.GetProperty("action").GetString()!;
        var domain = delivery.CapabilityId switch
        {
            BuiltInDeviceCapabilities.HomeAssistantCoverSet => "cover",
            BuiltInDeviceCapabilities.HomeAssistantLockSet => "lock",
            _ => "alarm_control_panel"
        };
        int? position = arguments.TryGetProperty("position", out var value) ? value.GetInt32() : null;
        var expected = action switch
        {
            "open_cover" => "open",
            "close_cover" => "closed",
            "set_cover_position" => position == 0 ? "closed" : "open",
            "lock" => "locked",
            "unlock" => "unlocked",
            "alarm_arm_home" => "armed_home",
            "alarm_arm_away" => "armed_away",
            "alarm_arm_night" => "armed_night",
            "alarm_disarm" => "disarmed",
            _ => throw new DomainException(409, "Unsupported sensitive Home Assistant action.")
        };
        bool Matches(JsonElement state) => HasState(state, expected) && (domain != "cover" ||
            MatchesCoverPosition(state, position, expected == "closed" ? 0 : 100));
        var before = await HomeStateAsync(entity, ct);
        Guard.Require(before.TryGetProperty("state", out var status) && status.ValueKind == JsonValueKind.String &&
            status.GetString() is not (null or "unavailable" or "unknown" or "jammed"),
            502, "Home Assistant did not report a usable state for the approved entity.");
        await authorizeDispatch(ct);
        var remaining = delivery.ExpiresAt - clock.GetUtcNow();
        Guard.Require(remaining > TimeSpan.Zero, 409, "The approved Home Assistant command expired before execution.");
        if (Matches(before)) return "Home Assistant already reports the requested state.";

        var data = new Dictionary<string, object> { ["entity_id"] = entity };
        if (position is { } target) data["position"] = target;
        if ((domain is "lock" or "alarm_control_panel") && config[$"HomeAssistant:ActionCodes:{entity}"] is { Length: > 0 } code)
            data["code"] = code;
        using var verification = CancellationTokenSource.CreateLinkedTokenSource(ct);
        verification.CancelAfter(remaining < TimeSpan.FromSeconds(30) ? remaining : TimeSpan.FromSeconds(30));
        try
        {
            await HomeActionAsync(entity, domain, action, data, verification.Token);
            while (true)
            {
                var state = await HomeStateAsync(entity, verification.Token);
                if (Matches(state)) return "Home Assistant reports the requested state.";
                Guard.Require(!HasState(state, "unavailable") && !HasState(state, "unknown") && !HasState(state, "jammed"),
                    502, "Home Assistant did not confirm the approved action.");
                await Task.Delay(TimeSpan.FromMilliseconds(500), verification.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new DomainException(502, "Home Assistant did not confirm the approved action before the verification deadline.");
        }
    }

    private static bool MatchesCoverPosition(JsonElement state, int? explicitPosition, int endpoint)
    {
        if (state.TryGetProperty("attributes", out var attributes) && attributes.ValueKind == JsonValueKind.Object &&
            attributes.TryGetProperty("current_position", out var position) && position.ValueKind != JsonValueKind.Null)
            return position.ValueKind == JsonValueKind.Number && position.TryGetInt32(out var actual) &&
                actual == (explicitPosition ?? endpoint);
        return explicitPosition is null;
    }

    private async Task<string> SetLightAsync(string entity, JsonElement arguments,
        Func<CancellationToken, Task> authorizeDispatch, CancellationToken ct)
    {
        var data = new Dictionary<string, object> { ["entity_id"] = entity };
        int? brightness = arguments.TryGetProperty("brightnessPct", out var percent) ? percent.GetInt32() : null;
        int? kelvin = arguments.TryGetProperty("colorTempKelvin", out var temperature) ? temperature.GetInt32() : null;
        var rgb = arguments.TryGetProperty("rgbColor", out var components)
            ? components.EnumerateArray().Select(x => x.GetInt32()).ToArray() : null;
        var off = brightness == 0;
        if (brightness is { } level && !off) data["brightness_pct"] = level;
        if (kelvin is { } colorTemperature) data["color_temp_kelvin"] = colorTemperature;
        if (rgb is not null) data["rgb_color"] = rgb;
        await authorizeDispatch(ct);
        await HomeActionAsync(entity, "light", off ? "turn_off" : "turn_on", data, ct);

        var reply = await HomeStateAsync(entity, ct);
        Guard.Require(HasState(reply, off ? "off" : "on"), 502, "Home Assistant did not report the requested light state.");
        if (off) return "Home Assistant reports that the light is off.";
        var attributes = HomeAttributes(reply);
        Guard.Require((brightness is null || NearInteger(attributes, "brightness", brightness.Value * 255m / 100, 1)) &&
            (kelvin is null || NearInteger(attributes, "color_temp_kelvin", kelvin.Value, 1)) &&
            (rgb is null || MatchesRgb(attributes, rgb)), 502, "Home Assistant did not report the requested light settings.");
        return "Home Assistant reports the requested light settings.";
    }

    private async Task<string> ActivateSceneAsync(string entity, Func<CancellationToken, Task> authorizeDispatch, CancellationToken ct)
    {
        var before = await HomeStateAsync(entity, ct);
        var previous = SceneActivation(before);
        Guard.Require(previous.HasValue || HasState(before, "unknown"), 502, "Home Assistant did not report a usable scene activation state.");
        await authorizeDispatch(ct);
        await HomeActionAsync(entity, "scene", "turn_on", new { entity_id = entity }, ct);
        var current = SceneActivation(await HomeStateAsync(entity, ct));
        Guard.Require(current.HasValue && (!previous.HasValue || current.Value > previous.Value),
            502, "Home Assistant did not report a new scene activation.");
        return "Home Assistant reports a new activation of the scene.";
    }

    private async Task<string> SetClimateAsync(string entity, decimal celsius, Func<CancellationToken, Task> authorizeDispatch,
        CancellationToken ct)
    {
        var before = HomeAttributes(await HomeStateAsync(entity, ct));
        var unit = await ClimateUnitAsync(entity, before, ct);
        var setpoint = unit == "°F" ? celsius * 9m / 5m + 32m : celsius;
        Guard.Require((!TryNumber(before, "min_temp", out var minimum) || setpoint >= minimum) &&
            (!TryNumber(before, "max_temp", out var maximum) || setpoint <= maximum), 409,
            "The requested setpoint is outside the thermostat's supported range.");
        await authorizeDispatch(ct);
        await HomeActionAsync(entity, "climate", "set_temperature", new { entity_id = entity, temperature = setpoint }, ct);
        var reply = HomeAttributes(await HomeStateAsync(entity, ct));
        Guard.Require(await ClimateUnitAsync(entity, reply, ct) == unit &&
            TryNumber(reply, "temperature", out var observed) && observed >= setpoint - 0.05m && observed <= setpoint + 0.05m,
            502, "Home Assistant did not report the requested target temperature.");
        return FormattableString.Invariant($"Home Assistant reports a target of {celsius:0.##} °C.");
    }

    private async Task<string> ClimateUnitAsync(string entity, JsonElement attributes, CancellationToken ct)
    {
        string? unit;
        if (attributes.TryGetProperty("temperature_unit", out var value))
            unit = value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        else
        {
            // Older Home Assistant releases expose the state machine's unit only in configuration.
            var (root, token) = HomeTarget(entity);
            using var request = new HttpRequestMessage(HttpMethod.Get, root + "/api/config");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var reply = await JsonAsync(request, ct);
            unit = reply.ValueKind == JsonValueKind.Object &&
                reply.TryGetProperty("unit_system", out var units) && units.ValueKind == JsonValueKind.Object &&
                units.TryGetProperty("temperature", out var configured) && configured.ValueKind == JsonValueKind.String
                ? configured.GetString() : null;
        }
        Guard.Require(unit is "°C" or "°F", 502, "Home Assistant did not report a supported thermostat temperature unit.");
        return unit;
    }

    private async Task HomeActionAsync(string entity, string domain, string action, object body, CancellationToken ct)
    {
        var (root, token) = HomeTarget(entity);
        using var request = new HttpRequestMessage(HttpMethod.Post, root + $"/api/services/{domain}/{action}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = JsonContent.Create(body);
        await JsonAsync(request, ct);
    }

    private static JsonElement HomeAttributes(JsonElement reply)
    {
        Guard.Require(reply.TryGetProperty("attributes", out var attributes) && attributes.ValueKind == JsonValueKind.Object,
            502, "Home Assistant did not report the requested entity attributes.");
        return attributes;
    }

    private static bool HasState(JsonElement reply, string expected) =>
        reply.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String && state.ValueEquals(expected);

    private static DateTimeOffset? SceneActivation(JsonElement reply) =>
        reply.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.String && state.GetString() is { } text &&
        text.Contains('T') && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed : null;

    private static bool TryNumber(JsonElement attributes, string key, out decimal number)
    {
        number = default;
        return attributes.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out number);
    }

    private static bool NearInteger(JsonElement attributes, string key, decimal expected, int tolerance) =>
        attributes.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number &&
        value.TryGetInt32(out var actual) && Math.Abs(actual - expected) <= tolerance;

    private static bool MatchesRgb(JsonElement attributes, int[] rgb) =>
        attributes.TryGetProperty("rgb_color", out var value) && value.ValueKind == JsonValueKind.Array &&
        value.GetArrayLength() == 3 && value.EnumerateArray().Select((x, index) =>
            x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out var actual) &&
            actual >= rgb[index] - 2 && actual <= rgb[index] + 2).All(matches => matches);
}
