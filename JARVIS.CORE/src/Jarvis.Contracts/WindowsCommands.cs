using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Jarvis.Contracts;

public static class WindowsCommandProtocol
{
    public const string Version = "1.0.0", App = "windows.app.open", Url = "windows.url.open", Media = "windows.media.control",
        Volume = "windows.volume.change", Lock = "windows.workstation.lock", Notification = "windows.notification.show", Files = "windows.files.find";
    public static bool AllowedLocked(string id) => id is Media or Lock;
    public static bool IsId(string? value) => value is not null && Regex.IsMatch(value, "^[a-z][a-z0-9_-]{0,39}$", RegexOptions.CultureInvariant);
    private static string Schema(string properties, string required) => "{\"type\":\"object\",\"properties\":{" + properties + "},\"required\":[" + required + "],\"additionalProperties\":false}";
    private const string Id = "{\"type\":\"string\",\"maxLength\":40}";
    public static IReadOnlyDictionary<string, string> Schemas { get; } = new System.Collections.ObjectModel.ReadOnlyDictionary<string, string>(new Dictionary<string, string> {
        [App] = Schema("\"appId\":" + Id, "\"appId\""), [Url] = Schema("\"urlId\":" + Id, "\"urlId\""),
        [Media] = Schema("\"action\":{\"type\":\"string\",\"enum\":[\"play\",\"pause\",\"next\"]}", "\"action\""),
        [Volume] = Schema("\"action\":{\"type\":\"string\",\"enum\":[\"up\",\"down\",\"mute\"]},\"steps\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":10}", "\"action\",\"steps\""),
        [Lock] = Schema("", ""), [Notification] = Schema("\"message\":{\"type\":\"string\",\"maxLength\":500}", "\"message\""),
        [Files] = Schema("\"folderId\":" + Id + ",\"name\":{\"type\":\"string\",\"maxLength\":100}", "\"folderId\",\"name\"")
    });
    public static WindowsActionArguments Parse(string capability, string json)
    {
        if (json.Length > 8000 || !Schemas.ContainsKey(capability)) throw new ArgumentException("Unknown or oversized Windows command.");
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 4 }); var root = document.RootElement;
        using var schema = JsonDocument.Parse(Schemas[capability]);
        var fields = schema.RootElement.GetProperty("required").EnumerateArray().Select(p => p.GetString()!).Order().ToArray();
        if (root.ValueKind != JsonValueKind.Object || !root.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(fields)) throw new ArgumentException("Invalid Windows argument schema.");
        string Text(string name, int maximum) { var value = root.GetProperty(name).GetString(); return value is { Length: > 0 } && value.Length <= maximum && !value.Any(char.IsControl) ? value : throw new ArgumentException("Invalid Windows argument."); }
        string Target(string name) { var value = Text(name, 40); return IsId(value) ? value : throw new ArgumentException("An allow-list ID is required, never a path or URL."); }
        string Query() { var value = root.GetProperty("name").GetString(); return value is not null && value.Length <= 100 && !value.Any(char.IsControl) && SafeQuery(value) ? value : throw new ArgumentException("Search names only, never paths."); }
        return capability switch {
            App => new(Target("appId")), Url => new(Target("urlId")),
            Media => Text("action", 10) is "play" or "pause" or "next" ? new(Action: Text("action", 10)) : throw new ArgumentException("Unknown media action."),
            Volume => Text("action", 10) is "up" or "down" or "mute" && root.GetProperty("steps").TryGetInt32(out var steps) && steps is >= 1 and <= 10 &&
                (Text("action", 10) != "mute" || steps == 1) ? new(Action: Text("action", 10), Steps: steps) : throw new ArgumentException("Invalid volume action."),
            Lock => new(), Notification => new(Message: Text("message", 500)),
            Files => new(Target("folderId"), Name: Query()),
            _ => throw new ArgumentException("Windows capability is not installed.")
        };
    }
    private static bool SafeQuery(string value) => !value.Contains("..", StringComparison.Ordinal) && value.IndexOfAny(['\\', '/', ':', '*', '?', '<', '>', '|']) < 0;
}

public sealed record WindowsActionArguments(string? TargetId = null, string? Action = null, int Steps = 0, string? Message = null, string? Name = null);
public sealed record DeviceVoiceAuthority(Guid SessionId, Guid SourceId, long AudioFence, DateTimeOffset ExpiresAt);

// Domain-separated delivery MAC; Core also checks the dispatch fence on the acknowledgement before execution.
public static class DeviceCommandProof
{
    public static string CredentialHash(string credential) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(credential)));
    public static string Sign(DeliveredDeviceCommand command, string credentialHash)
    {
        var message = FormattableString.Invariant($"jarvis-device-command-v1\n{command.Id:D}\n{command.DispatchRequestId:D}\n{command.TargetDeviceId}\n{command.CapabilityId}\n{command.CapabilityVersion}\n{command.ArgumentHash}\n{command.ExpiresAt.UtcTicks}\n{command.DispatchedAt.UtcTicks}");
        return Convert.ToHexString(HMACSHA256.HashData(Convert.FromHexString(credentialHash), Encoding.UTF8.GetBytes(message)));
    }
    public static bool Verify(DeliveredDeviceCommand command, string deviceId, string credential)
    {
        if (command.TargetDeviceId != deviceId || command.CoreSignature is not { Length: 64 } || command.ArgumentHash != CredentialHash(command.ArgumentsJson)) return false;
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(command.CoreSignature), Convert.FromHexString(Sign(command, CredentialHash(credential)))); }
        catch (FormatException) { return false; }
    }
    public static string ReceiptSignature(Guid command, SubmitDeviceReceiptRequest request, string credential)
    {
        var message = $"jarvis-device-receipt-v1\n{command:D}\n{request.DispatchRequestId:D}\n{request.RequestId:D}\n{request.Kind}\n{CredentialHash(request.PayloadJson)}";
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(credential), Encoding.UTF8.GetBytes(message)));
    }
}
