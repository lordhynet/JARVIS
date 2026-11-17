namespace Jarvis.Contracts;

public enum TextCapability { Conversation, JsonReasoning, GroundedPublicSearch }
public enum ReasoningDataClass { Public, Personal }
public sealed record ReasoningMessage(string Role, string Text);
public sealed record TextReasoningTask(Guid OperationId, Guid ScopeId, string Profile, TextCapability Capability,
    ReasoningDataClass DataClass, bool AllowCloudProcessing, string Instructions, IReadOnlyList<ReasoningMessage> Messages,
    UsageLane Lane = UsageLane.Background, int? OutputLimit = null);
public sealed record ModelRoute(string Provider, string Model, string Profile, string PolicyVersion, string RateCardRevision,
    DateOnly ReviewedAt, decimal InputUsdPerMillion, decimal OutputUsdPerMillion, int OutputLimit,
    TextCapability Capability = TextCapability.JsonReasoning, ReasoningDataClass DataClass = ReasoningDataClass.Personal, bool CloudConsent = false);
public sealed record GroundingCitation(string Url, string Title, int StartIndex, int EndIndex);
public sealed record TextReasoningResult(string Text, IReadOnlyList<GroundingCitation> Citations, ModelRoute Route);
public sealed record ProviderAttempt(Guid Id, Guid ScopeId, string Fingerprint, ModelRoute Route, Guid ReservationId,
    string Outcome, DateTimeOffset StartedAt, DateTimeOffset? CompletedAt = null, bool SourceDeleted = false,
    VoiceMode? VoiceMode = null);
