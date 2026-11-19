namespace Jarvis.Contracts;

public static class RecordingTranscriptStates
{
    public const string Quoted = "Quoted", Ready = "Ready", Preparing = "Preparing", Running = "Running", Pending = "Pending",
        Succeeded = "Succeeded", Failed = "Failed", Uncertain = "Uncertain", Paused = "Paused", Complete = "Complete", Partial = "Partial";
    public const string Timing = "Capture chunk intervals; not word timing or acoustic pause measurement";
}
public sealed record QuoteRecordingTranscriptRequest(int Revision, bool? Locked);
public sealed record AdvanceRecordingTranscriptRequest(Guid RequestId, Guid QuoteId, int JobRevision,
    bool ConfirmCloudProcessing, bool ConfirmQuote, bool? Locked, bool ContinueAfterReconciliation = false);
public sealed record RelabelRecordingSegmentRequest(int TranscriptRevision, Guid PieceId, string Speaker, bool? Locked);
public sealed record RecordingTranscriptPieceView(Guid Id, string Track, int Sequence, double StartSeconds, double EndSeconds,
    string Status, decimal MaximumUsd, decimal MaximumGbp, Guid? ReservationId = null);
public sealed record RecordingTranscriptProgress(Guid RecordingId, Guid QuoteId, int Revision, int RecordingRevision,
    string Status, string Model, decimal MaximumUsd, decimal MaximumGbp, DateTimeOffset QuoteExpiresAt,
    IReadOnlyList<RecordingTranscriptPieceView> Pieces, ImportedDocument? Transcript = null, string? ErrorCode = null,
    bool AudioDeletePending = false, string TimingSource = RecordingTranscriptStates.Timing);
