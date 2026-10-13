namespace Jarvis.Contracts;

/// <summary>How a tool may be used. Anything with a side effect defaults to RequiresConfirmation.</summary>
public enum ToolPermission
{
    Allowed,
    RequiresConfirmation,
    Denied
}

public enum ToolInvocationStatus
{
    Pending,
    AwaitingConfirmation,
    Succeeded,
    Failed,
    Rejected
}

/// <summary>
/// An audited request to run a tool (e.g. "turn on desk lamp").
/// ArgumentsJson is stored verbatim for the audit log.
/// </summary>
public sealed record ToolInvocation(
    Guid Id,
    Guid SessionId,
    string ToolName,
    string ArgumentsJson,
    ToolPermission Permission,
    ToolInvocationStatus Status,
    // True when the request came from email/web content rather than the user's own voice.
    bool TriggeredByExternalContent,
    DateTimeOffset RequestedAt);
