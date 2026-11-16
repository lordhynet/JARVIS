namespace Jarvis.Contracts;

public static class AgentRunStatus
{
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
    public const string Expired = "Expired";
    public const string NeedsReconciliation = "NeedsReconciliation";
    public const string SourceDeleted = "SourceDeleted";
}
