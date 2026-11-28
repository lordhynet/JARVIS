using System.Text.Json;
using System.Text.RegularExpressions;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Coaching;
using Jarvis.Orchestrator.Workflows;

namespace Jarvis.Orchestrator.Runtime;

public sealed class ImportService(IRuntimeStore store, TimeProvider clock, ICoreTransaction? transaction = null)
{
    public Task<ImportedDocument> ImportAsync(ImportDocumentRequest request, CancellationToken ct = default)
    {
        Guard.Require(request.RequestId != Guid.Empty && request.Kind is "cv" or "interview", 400, "Choose cv or interview and supply an operation ID.");
        Guard.Require(request.ConfirmStorageConsent && (request.Kind != "interview" || request.ConfirmRecordingAuthorised), 400, "Explicit storage consent and authorised interview recording/import are required.");
        var title = Guard.Text(request.Title, "Import title", 200); var text = Guard.Text(request.Text, "Imported text", 100000);
        var segments = request.Segments?.ToList() ?? [];
        Guard.Require(segments.Count <= 2000, 400, "Transcript segment limit exceeded.");
        double previous = 0;
        foreach (var segment in segments)
        {
            Guard.Require(segment.RecordingPieceId is null && segment.CaptureTrack is null, 400, "Capture provenance is assigned by Core recording transcription only.");
            Guard.Require(segment.Speaker is TranscriptSegment.UserSpeaker or "interviewer" or "unknown" && !string.IsNullOrWhiteSpace(segment.Text) && segment.Text.Length <= 10000 &&
                double.IsFinite(segment.StartSeconds) && double.IsFinite(segment.EndSeconds) && segment.StartSeconds >= previous && segment.EndSeconds > segment.StartSeconds && segment.EndSeconds <= 28800,
                400, "Segments require ordered non-overlapping finite timing, text and an explicit speaker label.");
            previous = segment.EndSeconds;
        }
        Guard.Require(segments.Count == 0 || request.Kind == "interview" && string.Join("\n", segments.Select(x => x.Text)).Trim() == text,
            400, "Timed segment text must match the imported transcript exactly, joined with newlines.");
        var hash = PrivacyData.Hash(JsonSerializer.Serialize(new { request.Kind, title, text, segments }));
        return store.UpdateAsync(s =>
        {
            if (s.ImportedDocuments.TryGetValue(request.RequestId, out var prior))
            { Guard.Require(prior.DeletedAt is null && prior.ContentHash == hash, 409, "Import ID belongs to different or deleted content."); return prior; }
            Guard.Require(s.ImportedDocuments.Count < 200 && s.ImportedDocuments.Values.Sum(x => x.Text.Length) + text.Length <= 2000000, 409, "Import storage limit reached.");
            var document = new ImportedDocument(request.RequestId, request.Kind, title, text, hash, 1, clock.GetUtcNow(), segments);
            s.ImportedDocuments.Add(document.Id, document); return document;
        }, ct);
    }

    // Recorded interviews use the existing import store. Their independently captured tracks
    // can overlap; ordinary externally supplied imports retain their original strict timing rules.
    internal static ImportedDocument SaveRecordingTranscript(RuntimeState state, InterviewRecording recording,
        IReadOnlyList<TranscriptSegment> segments, bool partial, DateTimeOffset at)
    {
        Guard.Require(recording.ConfirmStorageConsent && recording.ConfirmRecordingAuthorised && recording.ConsentAt is not null,
            403, "The recording's storage and authorization confirmations are required.");
        Guard.Require(segments.Count is > 0 and <= 360 && segments.Select(s => s.RecordingPieceId).Distinct().Count() == segments.Count,
            400, "A recorded transcript needs bounded distinct capture pieces.");
        var ordered = segments.OrderBy(s => s.StartSeconds).ThenBy(s => s.CaptureTrack, StringComparer.Ordinal).ToArray();
        foreach (var segment in ordered) Guard.Require(segment.RecordingPieceId is not null && recording.Tracks.Contains(segment.CaptureTrack!) &&
            segment.Speaker is "user" or "interviewer" or "unknown" && !string.IsNullOrWhiteSpace(segment.Text) && segment.Text.Length <= 10000 &&
            double.IsFinite(segment.StartSeconds) && double.IsFinite(segment.EndSeconds) && segment.StartSeconds >= 0 &&
            segment.EndSeconds > segment.StartSeconds && segment.EndSeconds <= RecordingProtocol.MaximumSeconds,
            400, "Recorded segments need capture provenance, finite intervals, text and an explicit speaker label.");
        foreach (var track in ordered.GroupBy(s => s.CaptureTrack)) {
            double previous = 0;
            foreach (var segment in track) { Guard.Require(segment.StartSeconds >= previous, 400, "Capture intervals on one track cannot overlap."); previous = segment.EndSeconds; }
        }
        var id = AgentRuntime.StableId("recording-transcript-v1:" + recording.Id.ToString("N"));
        state.ImportedDocuments.TryGetValue(id, out var prior);
        Guard.Require(prior is null || prior.DeletedAt is null && prior.SourceRecordingId == recording.Id, 409, "This transcript was deleted or its identity belongs to other content.");
        var text = string.Join("\n", ordered.Select(s => s.Text));
        Guard.Require(text.Length <= 500000 && (prior is not null || state.ImportedDocuments.Count < 200) &&
            state.ImportedDocuments.Values.Where(d => d.Id != id).Sum(d => d.Text.Length) + text.Length <= 2000000, 409, "Import storage limit reached.");
        AgentRuntime.InvalidateSource(state, "import", id);
        var document = new ImportedDocument(id, "interview", recording.Title, text,
            PrivacyData.Hash(JsonSerializer.Serialize(new { recording.Id, text, segments = ordered })), (prior?.Revision ?? 0) + 1,
            prior?.ImportedAt ?? at, ordered, SourceRecordingId: recording.Id, Partial: partial, TimingSource: RecordingTranscriptStates.Timing);
        state.ImportedDocuments[id] = document; return document;
    }

    // Attaching a claim edits the story, so generated practice shown that story is purged in the same transaction.
    public Task<SourceClaim> AttachClaimAsync(Guid documentId, AttachSourceClaimRequest request, CancellationToken ct = default) =>
        Jarvis.Orchestrator.Training.GeneratedPracticeTriggers.UpdateAsync(store, Jarvis.Orchestrator.Training.PracticeLineage.Story, request.StoryId, s =>
    {
        Guard.Require(request.RequestId != Guid.Empty, 400, "Claim operation ID required.");
        Guard.Require(s.ImportedDocuments.TryGetValue(documentId, out var document) && document.DeletedAt is null && document.Kind == "cv", 404, "CV document not found.");
        Guard.Require(document.Revision == request.ExpectedDocumentRevision, 409, "CV source changed.");
        Guard.Require(request.Start >= 0 && request.Length is > 0 and <= 2000 && request.Start <= document.Text.Length - request.Length, 400, "Claim range is invalid.");
        var quote = document.Text.Substring(request.Start, request.Length);
        if (s.SourceClaims.TryGetValue(request.RequestId, out var replay))
        { Guard.Require(replay.DocumentId == documentId && replay.DocumentRevision == document.Revision && replay.StoryId == request.StoryId && replay.Start == request.Start && replay.Length == request.Length, 409, "Claim ID was reused."); return replay; }
        Guard.Require(s.Stories.TryGetValue(request.StoryId, out var story), 404, "Story not found.");
        Guard.Require(story.Version == request.ExpectedStoryVersion, 409, "Story changed. Reload before attaching a claim.");
        var claims = story.Content.CvClaims ?? [];
        Guard.Require(claims.Length < 30 && !claims.Contains(quote), 409, "Claim is already attached or the claim limit was reached.");
        AgentRuntime.InvalidateSource(s, "story", story.Id);
        s.Stories[story.Id] = story with { Content = story.Content with { CvClaims = [..claims, quote] }, Version = story.Version + 1, UpdatedAt = clock.GetUtcNow() };
        var result = new SourceClaim(request.RequestId, documentId, document.Revision, story.Id, quote, request.Start, request.Length, clock.GetUtcNow());
        s.SourceClaims.Add(result.Id, result); return result;
    }, clock, ct);

    public Task<bool> DeleteAsync(Guid id, int revision, CancellationToken ct = default)
    {
        async Task<bool> Delete(RuntimeState s, Func<Guid, Func<ConversationSession, ConversationSession>, Task<ConversationSession?>>? update,
            Func<IReadOnlySet<Guid>, DateTimeOffset, Task<IReadOnlySet<Guid>>>? tombstoneGenerated) {
        Guard.Require(s.ImportedDocuments.TryGetValue(id, out var document) && document.DeletedAt is null, 404, "Import not found.");
        Guard.Require(document.Revision == revision, 409, "Import changed. Reload before deleting.");
        var sessions = document.SourceRecordingId is { } recording ? s.VoiceCoachings.Values.Where(p => p.SourceRecordingId == recording).Select(p => p.SessionId).ToArray() : [];
        await Jarvis.Orchestrator.Recordings.RecordedPracticePrivacy.RedactSessionsAsync(s, update, sessions, clock.GetUtcNow());
        var before = Jarvis.Orchestrator.Training.GeneratedPracticeTriggers.StoryVersions(s);
        var at = clock.GetUtcNow();
        Purge(s, document, at);
        // Removing CV claims edits stories; generated practice shown those stories is purged in this same transaction.
        var affected = Jarvis.Orchestrator.Training.GeneratedPracticeTriggers.AffectedByStoryChanges(s, before);
        if (affected.Count > 0)
        {
            Guard.Require(tombstoneGenerated is not null, 503, "Deleting this import requires the composite transaction because practice depends on it.");
            var questions = await tombstoneGenerated(affected, at);
            Jarvis.Orchestrator.Training.GeneratedPracticeTriggers.PurgeRuntime(s, questions, affected, at);
        }
        return true;
        }
        return transaction is not null ? transaction.ExecuteAsync(w => Delete(w.Runtime, w.UpdateSessionAsync, w.TombstoneGeneratedQuestionsInAsync), ct)
            : store.UpdateAsync(s => Delete(s, null, null).GetAwaiter().GetResult(), ct);
    }

    public static void Purge(RuntimeState s, ImportedDocument document, DateTimeOffset at)
    {
        if (document.SourceRecordingId is { } recording) Jarvis.Orchestrator.Recordings.RecordedPracticePrivacy.Purge(s, recording, at);
        AgentRuntime.InvalidateSource(s, "import", document.Id);
        foreach (var group in s.SourceClaims.Values.Where(x => x.DocumentId == document.Id).GroupBy(x => x.StoryId).ToList())
        {
            if (s.Stories.TryGetValue(group.Key, out var story))
            {
                var removed = group.Select(x => x.Quote).ToHashSet();
                s.Stories[story.Id] = story with { Content = story.Content with { CvClaims = (story.Content.CvClaims ?? []).Where(x => !removed.Contains(x)).ToArray() }, Version = story.Version + 1, UpdatedAt = at };
                foreach (var attempt in s.CoachingAttempts.Values.Where(x => x.StoryId == story.Id && x.DeletedAt is null).ToList()) CoachingService.Purge(s, attempt, at);
                AgentRuntime.InvalidateSource(s, "story", story.Id);
            }
            foreach (var claim in group) s.SourceClaims.Remove(claim.Id);
        }
        foreach (var assessment in s.AiCoachAssessments.Values.Where(x => x.AttemptId == document.Id).ToList()) s.AiCoachAssessments.Remove(assessment.Id);
        ReasoningLifecycle.Retire(s, document.Id);
        s.ImportedDocuments[document.Id] = document with { Title = "", Text = "", Segments = [], DeletedAt = at, Revision = document.Revision + 1 };
    }

    public static InterviewArchiveReview Review(ImportedDocument document)
    {
        Guard.Require(document.DeletedAt is null && document.Kind == "interview", 404, "Interview transcript not found.");
        int Words(string value) => Regex.Matches(value, @"\b[\p{L}\p{N}]+\b").Count;
        var questions = document.Text.Split('\n').Where(x => x.Contains('?')).Select(x => x.Trim()).Take(50).ToList();
        var user = document.Segments.Where(x => x.Speaker == TranscriptSegment.UserSpeaker).ToList();
        var seconds = user.Sum(x => x.EndSeconds - x.StartSeconds);
        var pauses = user.Zip(user.Skip(1)).Count(x => x.Second.StartSeconds - x.First.EndSeconds >= 1 &&
            !document.Segments.Any(other => other.Speaker != TranscriptSegment.UserSpeaker && other.StartSeconds >= x.First.EndSeconds && other.EndSeconds <= x.Second.StartSeconds));
        return new(document.Id, questions, Words(document.Text), seconds > 0 ? Math.Round(user.Sum(x => Words(x.Text)) * 60 / seconds, 1) : null,
            document.SourceRecordingId is null && user.Count > 1 ? pauses : null, document.TimingSource ?? (seconds > 0 ? "Imported speaker timing; not independently verified acoustic measurement" : "No timing; pace and pauses unavailable"),
            ["Re-answer one extracted question using a real project example.", "Identify personal ownership and one trade-off.", "Practise the weakest answer again after a short break."]);
    }
}
