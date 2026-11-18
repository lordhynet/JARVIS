using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Api;

internal static class OwnerDisplayName
{
    public static string Read(IConfiguration config)
    {
        var name = (config[ConfigKeys.OwnerDisplayName] ?? "").Trim();
        Guard.Require(name.Length <= 80 && !name.Any(char.IsControl), 503, "Owner display name must be at most 80 characters without control characters.");
        return name;
    }
}
