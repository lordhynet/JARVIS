namespace Jarvis.Contracts;

public sealed record TranscriptSegment(string Speaker, string Text, double StartSeconds, double EndSeconds);
public sealed record ImportDocumentRequest(Guid RequestId, string Kind, string Title, string Text,
    bool ConfirmStorageConsent, bool ConfirmRecordingAuthorised = false, IReadOnlyList<TranscriptSegment>? Segments = null);
public sealed record ImportedDocument(Guid Id, string Kind, string Title, string Text, string ContentHash,
    int Revision, DateTimeOffset ImportedAt, IReadOnlyList<TranscriptSegment> Segments, DateTimeOffset? DeletedAt = null);
public sealed record ImportedDocumentSummary(Guid Id, string Kind, string Title, int Revision, DateTimeOffset ImportedAt, int Characters);
public sealed record SourceClaim(Guid Id, Guid DocumentId, int DocumentRevision, Guid StoryId, string Quote, int Start, int Length, DateTimeOffset At);
public sealed record AttachSourceClaimRequest(Guid RequestId, Guid StoryId, int ExpectedStoryVersion, int ExpectedDocumentRevision, int Start, int Length);
public sealed record InterviewArchiveReview(Guid DocumentId, IReadOnlyList<string> Questions, int WordCount,
    double? TimedUserWordsPerMinute, int? UserPausesAtLeastOneSecond, string TimingSource, IReadOnlyList<string> SuggestedDrills);
