using System.Globalization;
using System.Text.Json;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Api.Voice;

internal sealed record HomeVoiceRequest(string Name, string Capability, string Action, string? Setting = null, object? Value = null)
{
    private const string Invalid = "Say one direct home request with a named entity and an exact setting.";
    private static readonly Dictionary<string, int[]> Colours = new(StringComparer.OrdinalIgnoreCase)
    {
        ["red"] = [255, 0, 0], ["green"] = [0, 255, 0], ["blue"] = [0, 0, 255], ["white"] = [255, 255, 255],
        ["yellow"] = [255, 255, 0], ["cyan"] = [0, 255, 255], ["purple"] = [128, 0, 128], ["orange"] = [255, 165, 0],
        ["pink"] = [255, 192, 203]
    };
    private static readonly string[] SmallNumbers = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine",
        "ten", "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"];
    private static readonly string[] Tens = ["twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];

    public string Arguments(string entityId)
    {
        var payload = new Dictionary<string, object> { ["entityId"] = entityId };
        if (BuiltInDeviceCapabilities.IsSensitiveHomeAssistant(Capability)) payload["action"] = Action;
        if (Setting is not null) payload[Setting] = Value!;
        return JsonSerializer.Serialize(payload);
    }

    public static string CapabilityFor(string action) => action switch
    {
        "turn" => BuiltInDeviceCapabilities.HomeAssistantTurn,
        "light_set" => BuiltInDeviceCapabilities.HomeAssistantLightSet,
        "scene" => BuiltInDeviceCapabilities.HomeAssistantSceneActivate,
        "climate_set" => BuiltInDeviceCapabilities.HomeAssistantClimateSet,
        "cover" => BuiltInDeviceCapabilities.HomeAssistantCoverSet,
        "lock" => BuiltInDeviceCapabilities.HomeAssistantLockSet,
        "alarm" => BuiltInDeviceCapabilities.HomeAssistantAlarmSet,
        _ => throw new DomainException(400, "The voice home tool does not support that action.")
    };

    public static HomeVoiceRequest Parse(string text, string action)
    {
        Guard.Require(text.Length is > 0 and <= 200 && !text.Any(char.IsControl), 400, Invalid);
        var words = text.Trim().TrimEnd('.', '!').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var start = 0;
        var end = words.Length;
        bool Salutation(int index, string word) => index < end && Is(words[index].TrimEnd(','), word);
        if (Salutation(0, "hey") && Salutation(1, "jarvis")) start = 2;
        else if (Salutation(0, "jarvis")) start = 1;
        if (start < end && Is(words[start], "please")) start++;
        if (start < end && Is(words[end - 1], "please")) end--;
        Guard.Require(end > start, 400, Invalid);
        var command = words[start..end];
        var capability = CapabilityFor(action);
        if (action == "turn") return Turn(command, capability);
        if (action == "lock")
        {
            Guard.Require(command.Length >= 2 && (Is(command[0], "lock") || Is(command[0], "unlock")), 400, Invalid);
            return new(ReadName(WithoutArticle(command[1..])), capability, command[0].ToLowerInvariant());
        }
        if (action == "alarm") return Alarm(command, capability);
        if (action == "cover" && (Is(command[0], "open") || Is(command[0], "close")))
            return new(ReadName(WithoutArticle(command[1..])), capability, Is(command[0], "open") ? "open_cover" : "close_cover");
        if (action == "scene")
        {
            Guard.Require(command.Length >= 2 && Is(command[0], "activate"), 400, Invalid);
            var name = WithoutArticle(command[1..]);
            if (name.Length > 1 && Is(name[0], "scene")) name = name[1..];
            return new(ReadName(name), capability, action);
        }

        var to = Array.FindLastIndex(command, token => Is(token, "to"));
        Guard.Require(Is(command[0], "set") && to >= 2 && to < command.Length - 1, 400, Invalid);
        var target = WithoutArticle(command[1..to]);
        var value = command[(to + 1)..];
        if (action == "cover")
        {
            var explicitPercent = Percent(value);
            Guard.Require(explicitPercent is not null, 400, "Say the cover position as a percentage.");
            return new(ReadName(WithoutSuffix(target, "position")), capability, "set_cover_position",
                "position", Integer(explicitPercent, 0, 100));
        }
        return action == "light_set" ? Light(target, value, capability) : Climate(target, value, capability);
    }

    private static HomeVoiceRequest Alarm(string[] words, string capability)
    {
        if (Is(words[0], "disarm"))
            return new(ReadName(WithoutArticle(words[1..])), capability, "alarm_disarm");
        Guard.Require(words.Length >= 5 && Is(words[0], "arm") && Is(words[^3], "in") && Is(words[^1], "mode"),
            400, "Say which granted alarm to arm and say home, away or night mode.");
        var mode = words[^2].ToLowerInvariant();
        Guard.Require(mode is "home" or "away" or "night", 400, "The alarm mode must be home, away or night.");
        return new(ReadName(WithoutArticle(words[1..^3])), capability, "alarm_arm_" + mode);
    }

    private static HomeVoiceRequest Turn(string[] words, string capability)
    {
        Guard.Require(words.Length >= 3 && Is(words[0], "turn"), 400, Invalid);
        static string? Direction(string word) => Is(word, "on") ? "turn_on" : Is(word, "off") ? "turn_off" : null;
        var direction = Direction(words[1]);
        var target = direction is not null ? words[2..] : words[1..^1];
        direction ??= Direction(words[^1]);
        Guard.Require(direction is not null, 400, Invalid);
        return new(ReadName(WithoutArticle(target)), capability, direction, "action", direction);
    }

    private static HomeVoiceRequest Light(string[] name, string[] value, string capability)
    {
        const string action = "light_set";
        Guard.Require(name.Length > 0, 400, Invalid);
        var last = value[^1];
        if (Percent(value) is { } numeric)
            return new(ReadName(WithoutSuffix(name, "brightness")), capability, action, "brightnessPct", Integer(numeric, 0, 100));
        if (Is(last, "kelvin") || Is(last, "k"))
        {
            name = WithoutSuffix(name, "temperature");
            name = WithoutSuffix(name, "colour", "color");
            return new(ReadName(name), capability, action, "colorTempKelvin", Integer(value[..^1], 1000, 10000));
        }
        name = WithoutSuffix(name, "colour", "color");
        if (value.Length == 1 && Colours.TryGetValue(value[0], out var named))
            return new(ReadName(name), capability, action, "rgbColor", named);
        Guard.Require(value.Length == 4 && Is(value[0], "rgb"), 400, Invalid);
        var components = value.Skip(1).Select(word => Integer([word.TrimEnd(',')], 0, 255)).ToArray();
        return new(ReadName(name), capability, action, "rgbColor", components);
    }

    private static string[]? Percent(string[] words)
    {
        var last = words[^1];
        if (last.EndsWith('%')) return words[..^1].Append(last[..^1]).ToArray();
        if (Is(last, "percent")) return words[..^1];
        return words.Length > 1 && Is(last, "cent") && Is(words[^2], "per") ? words[..^2] : null;
    }

    private static HomeVoiceRequest Climate(string[] name, string[] value, string capability)
    {
        if (Is(value[^1], "celsius")) value = value[..^1];
        Guard.Require(value.Length >= 2 && (Is(value[^1], "degrees") || Is(value[^1], "degree")),
            400, "Say the target temperature in degrees Celsius.");
        var temperature = Number(value[..^1]);
        return new(ReadName(name), capability, "climate_set", "temperatureC", temperature);
    }

    private static int Integer(string[] words, int min, int max)
    {
        var value = Number(words);
        Guard.Require(value >= min && value <= max && decimal.Truncate(value) == value, 400, Invalid);
        return (int)value;
    }

    private static decimal Number(string[] words)
    {
        var text = string.Join(' ', words);
        if (decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number))
            return number;
        var parts = text.ToLowerInvariant().Split([' ', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 1)
        {
            var small = Array.IndexOf(SmallNumbers, parts[0]);
            if (small >= 0) return small;
            var ten = Array.IndexOf(Tens, parts[0]);
            if (ten >= 0) return (ten + 2) * 10;
        }
        if (parts.Length == 2)
        {
            var ten = Array.IndexOf(Tens, parts[0]);
            var one = Array.IndexOf(SmallNumbers, parts[1]);
            if (ten >= 0 && one is > 0 and < 10) return (ten + 2) * 10 + one;
            if (Is(parts[0], "one") && Is(parts[1], "hundred")) return 100;
        }
        throw new DomainException(400, "Say an exact number for the home setting.");
    }

    private static string[] WithoutArticle(string[] words) => words.Length > 0 && Is(words[0], "the") ? words[1..] : words;
    private static string[] WithoutSuffix(string[] words, params string[] endings) =>
        words.Length > 1 && endings.Any(ending => Is(words[^1], ending)) ? words[..^1] : words;

    private static string ReadName(string[] words)
    {
        var name = string.Join(' ', words);
        Guard.Require(name.Length > 0 && name.IndexOfAny([',', ';', '!', '?']) < 0, 400, Invalid);
        return name;
    }

    private static bool Is(string value, string expected) => value.Equals(expected, StringComparison.OrdinalIgnoreCase);
}
