namespace Jarvis.Contracts;

public sealed record ProviderResult(Guid Id, Guid ScopeId, string Fingerprint, string Json, DateTimeOffset At);
public sealed record AiAssessmentRequest(Guid RequestId, int ExpectedRevision, int TextRevision, bool AllowCloudProcessing, string TimeZone = "Europe/London");
public sealed record AiCoachAssessment(Guid Id, Guid AttemptId, int AnswerVersion, IReadOnlyList<DimensionScore> Scores,
    string Summary, IReadOnlyList<string> FollowUps, string Model, DateTimeOffset At);
public sealed record VoiceStartRequest(Guid SessionId, string DeviceId, bool AllowCloudProcessing, bool? Locked = null);
public sealed record CompanionVoiceRequest(Guid RequestId, string DeviceId, bool AllowCloudProcessing, bool? Locked = null);
public sealed record CompanionVoiceSession(Guid SessionId);
public sealed record AudioLease(Guid SessionId, string DeviceId, long Fence, DateTimeOffset ExpiresAt,
    bool Locked = false, bool? ReportedLocked = null);
public sealed record VoiceTicket(string Ticket, AudioLease Lease, int WindowSeconds, int SampleRate);
public sealed record WakeCandidateRequest(Guid RequestId, string DeviceId, bool AllowCloudProcessing, bool? Locked = null);
public sealed record WakeCandidateResponse(Guid SessionId, VoiceTicket Voice);
public sealed record VoiceTicketRecord(string Hash, AudioLease Lease, DateTimeOffset ExpiresAt, int WindowSeconds = 60);
public sealed record VoiceTurn(Guid Id, Guid SessionId, string Role, string Text, double? SpeechSeconds, DateTimeOffset At);
public sealed record AiCapabilityStatus(bool Ready, string Model, string Detail);
