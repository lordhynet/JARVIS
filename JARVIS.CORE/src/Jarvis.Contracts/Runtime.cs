namespace Jarvis.Contracts;

public sealed record RegisterDeviceRequest(string DeviceId, string Name, DeviceKind Kind, DeviceCapabilities Capabilities);
public sealed record RegisteredDevice(DeviceRegistration Registration, DateTimeOffset LastSeenAt, bool CapabilitiesVerified);
public sealed record DeviceView(RegisteredDevice Device, bool Online, string OwnerDisplayName = "");
public sealed record SessionTransitionRequest(SessionStatus Status);
public sealed record StartHandoffRequest(string FromDeviceId, string ToDeviceId, string Summary);
public sealed record HandoffActionRequest(Guid HandoffId, string DeviceId);
public sealed record SessionHandoff(Guid Id, string FromDeviceId, string ToDeviceId,
    HandoffState State, DateTimeOffset RequestedAt, DateTimeOffset ExpiresAt);
public sealed record ChatMessage(Guid RequestId, string Role, string Text, DateTimeOffset At, bool IsVoice = false, double? SpeechSeconds = null, bool Restricted = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] SpeechTiming? Timing = null);
public sealed record ChatRequest(Guid RequestId, string Text);
public sealed record CreateLearningRequest(LearningDomain Domain, string Prompt, string Answer);
public sealed record ReviewLearningRequest(Guid RequestId, ReviewRating Rating, double ResponseSeconds);
public sealed record LearningReview(Guid RequestId, ReviewEvent Event, LearningItem Result);
public sealed record HomeActionRequest(Guid RequestId, Guid SessionId, string EntityId, string Action, bool TriggeredByExternalContent = false);
public sealed record AuditedTool(ToolInvocation Invocation, string? Result, DateTimeOffset? CompletedAt);
public sealed record CalendarEntry(string Id, string Title, string Start, string End, bool AllDay, string Provider);
public sealed record TransitLine(string Id, string Name, IReadOnlyList<string> Status);
public sealed record IntegrationStatus(string Name, bool Configured, string Detail);
public sealed record BriefingSection(string Name, bool Available, object? Data, string? Error);
public sealed record DailyBriefing(DateTimeOffset GeneratedAt, IReadOnlyList<BriefingSection> Sections);
