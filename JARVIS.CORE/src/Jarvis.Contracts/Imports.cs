using System.Text.Json.Serialization;

namespace Jarvis.Contracts;

public sealed record TranscriptSegment(string Speaker, string Text, double StartSeconds, double EndSeconds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? RecordingPieceId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CaptureTrack = null)
{
    public const string UserSpeaker = "user";
}
public sealed record ImportDocumentRequest(Guid RequestId, string Kind, string Title, string Text,
    bool ConfirmStorageConsent, bool ConfirmRecordingAuthorised = false, IReadOnlyList<TranscriptSegment>? Segments = null);
public sealed record ImportedDocument(Guid Id, string Kind, string Title, string Text, string ContentHash,
    int Revision, DateTimeOffset ImportedAt, IReadOnlyList<TranscriptSegment> Segments, DateTimeOffset? DeletedAt = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? SourceRecordingId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Partial = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TimingSource = null);
public sealed record ImportedDocumentSummary(Guid Id, string Kind, string Title, int Revision, DateTimeOffset ImportedAt, int Characters);
public sealed record SourceClaim(Guid Id, Guid DocumentId, int DocumentRevision, Guid StoryId, string Quote, int Start, int Length, DateTimeOffset At);
public sealed record AttachSourceClaimRequest(Guid RequestId, Guid StoryId, int ExpectedStoryVersion, int ExpectedDocumentRevision, int Start, int Length);
public sealed record InterviewArchiveReview(Guid DocumentId, IReadOnlyList<string> Questions, int WordCount,
    double? TimedUserWordsPerMinute, int? UserPausesAtLeastOneSecond, string TimingSource, IReadOnlyList<string> SuggestedDrills);
/// <summary>B3-5 v0: a deterministic, read-only interview prep pack computed from an imported job advert.</summary>
/// <summary>Readiness and Level come from the deterministic Story readiness checker (0-100; draft, usable, interview-ready).</summary>
public sealed record PrepStoryMatch(Guid StoryId, string Title, int Readiness = 0, string Level = "draft");
/// <summary>Gap: no story matches. NeedsWork: stories match but every one is still a draft.</summary>
public sealed record PrepRequirement(string Key, string Title, string AdvertQuote, IReadOnlyList<PrepStoryMatch> Stories, bool Gap,
    bool NeedsWork = false);
public sealed record InterviewPrepPack(Guid DocumentId, string Title, IReadOnlyList<PrepRequirement> Requirements,
    IReadOnlyList<string> LikelyQuestions, int Gaps, string Note, string? Fingerprint = null, long? DataEpoch = null);
/// <summary>Turn selected prep-pack questions (by index) into review cards. Repeats never duplicate a card.</summary>
public sealed record CreatePrepPackCardsRequest(IReadOnlyList<int> Questions, string? ExpectedPackFingerprint = null, long? DataEpoch = null);
/// <summary>Cards are the original creation results; Existing counts questions already turned into cards before.</summary>
public sealed record PrepPackCards(int Created, int Existing, int Retired, IReadOnlyList<LearningItem> Cards);
