namespace Jarvis.Api;

internal static class OwnerDisplayName
{
    public static string Read(IConfiguration config, ILogger? logger = null)
    {
        var name = config[ConfigKeys.OwnerDisplayName] ?? "";
        if (name.Trim().Length > 80 || name.Any(char.IsControl)) {
            logger?.LogWarning("Owner:DisplayName is invalid; using the generic greeting. Use at most 80 characters without control characters.");
            return "";
        }
        return name.Trim();
    }
}
