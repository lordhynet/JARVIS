namespace Jarvis.Contracts;

public static class ProviderAttemptOutcome
{
    public const string Preparing = "Preparing";
    public const string Dispatched = "Dispatched";
    public const string Cached = "Cached";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Refused = "Refused";
    public const string Uncertain = "Uncertain";
}
