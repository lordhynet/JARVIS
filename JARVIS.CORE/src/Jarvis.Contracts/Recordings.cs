namespace Jarvis.Contracts;

public static class RecordingProtocol
{
    public const string Capture = "/api/recording-capture";
    public const int SampleRate = 16000, ChunkSeconds = 60, MaximumSeconds = 10800;
    public const string Consent = "I have told the interviewer, or I am otherwise entitled to record this conversation, and it is for my own practice only.";
}
public sealed record PrepareRecordingRequest(Guid RequestId, string DeviceId, string Title, bool ConfirmStorageConsent,
    bool ConfirmRecordingAuthorised, bool DeleteAudioAfterTranscription = false, bool? Locked = null);
public sealed record RecordingAudioBlock(Guid RecordingId, DateTimeOffset Until);
public sealed record InterviewRecording(Guid Id, string DeviceId, string Title, string Status, IReadOnlyList<string> Tracks,
    DateTimeOffset PreparedAt, DateTimeOffset OfferExpiresAt, DateTimeOffset? StartedAt = null, DateTimeOffset? StoppedAt = null,
    DateTimeOffset? ConsentAt = null, DateTimeOffset? AudioRetainUntil = null, bool DeleteAudioAfterTranscription = false,
    int DurationSamples = 0, long AudioBytes = 0, bool AudioDeleted = false, Guid? TranscriptId = null, int Revision = 1,
    bool ConfirmStorageConsent = false, bool ConfirmRecordingAuthorised = false, string? ConsentVersion = null);
public sealed record RecordingCaptureRequest(string Action, string DeviceId, Guid? RecordingId = null, bool? Locked = null,
    bool ConfirmStorageConsent = false, bool ConfirmRecordingAuthorised = false, string? Track = null, int? Sequence = null,
    string? Sha256 = null, string? AudioBase64 = null, IReadOnlyDictionary<string, int>? FinalCounts = null, bool Discontinuous = false);
public sealed record RecordingCaptureOffer(Guid Id, string Status, IReadOnlyList<string> Tracks, DateTimeOffset ExpiresAt,
    int ChunkSeconds = RecordingProtocol.ChunkSeconds, int MaximumSeconds = RecordingProtocol.MaximumSeconds);
public sealed record RecordingChunkAck(string Track, int Sequence, string Sha256);
public sealed record RecordingCaptureResult(bool Ready, IReadOnlyList<RecordingCaptureOffer> Offers,
    InterviewRecording? Recording = null, IReadOnlyList<RecordingChunkAck>? Acknowledged = null);
