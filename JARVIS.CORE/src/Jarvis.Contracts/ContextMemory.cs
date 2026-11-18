namespace Jarvis.Contracts;

public enum MemorySensitivity { Public, Personal, Sensitive }
public sealed record ContextMemoryItem(Guid Id, string DomainKey, Guid DomainVersionId, string Text, AgentSourceRef Source,
    Guid? SessionId, MemorySensitivity Sensitivity, DateTimeOffset RecordedAt, DateTimeOffset ExpiresAt);
public sealed record SaveContextMemoryRequest(Guid RequestId, string SourceKind, Guid? SourceId, string? Text,
    DateTimeOffset ExpiresAt, int Start = 0, int Length = 0, MemorySensitivity Sensitivity = MemorySensitivity.Personal);
