using System.Text.Json;
using System.Text.RegularExpressions;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Sessions;
using Jarvis.Orchestrator.Training;

namespace Jarvis.Orchestrator.Runtime;

public sealed class VoiceRecallService(IRuntimeStore store, ISessionReader sessions, TimeProvider clock)
{
    private static string Fingerprint(LearningItem card) => PrivacyData.Hash(JsonSerializer.Serialize(card));
    private static VoiceRecall? Active(RuntimeState s, Guid session) => s.VoiceRecalls.Values
        .Where(r => r.SessionId == session && r.Status is "Question" or "Rating").OrderByDescending(r => r.CreatedAt).FirstOrDefault();
    public async Task<VoiceRecallView?> RunAsync(Guid sessionId, string action, Guid sourceId, string rating, CancellationToken ct)
    {
        Guard.Require(action is "start" or "current" or "reveal" or "rate" or "stop", 400, "Unsupported recall action.");
        Guard.Require(action == "rate" || rating == "None", 400, "A rating is only valid after recall and reference reveal.");
        var session = await sessions.GetAsync(sessionId, ct);
        Guard.Require(session is { Status: SessionStatus.Active }, 409, "Resume the conversation before reviewing cards.");
        var source = session.Messages?.LastOrDefault(m => m.Role == ChatRoles.User);
        if (action != "current") Guard.Require(source is not null && !source.Restricted && source.RequestId == sourceId, 409, "Use the latest unrestricted captured user speech for that review action.");
        return await store.UpdateAsync(s =>
        {
            Guard.Require(!s.RetiredSessions.Contains(sessionId), 409, "This conversation was retired.");
            var now = clock.GetUtcNow(); var active = Active(s, sessionId);
            var startId = VoicePracticeService.Operation("recall-start", sessionId, sourceId);
            // Completed operations replay without advancing another card or scheduling twice.
            if (action == "start" && s.VoiceRecalls.TryGetValue(startId, out var started)) return View(s, started);
            if (action == "rate" && s.VoiceRecalls.Values.FirstOrDefault(r => r.SessionId == sessionId && r.RatingUtteranceId == sourceId) is { } rated)
            {
                Guard.Require(rated.Rating?.ToString() == rating, 409, "That utterance already recorded a different rating.");
                return View(s, rated);
            }
            if (action == "current") return active is null ? null : View(s, active);
            if (action == "start")
            {
                if (active is not null) return View(s, active);
                Guard.Require(s.VoiceRecalls.Count < 1000, 409, "Voice recall history is full; apply retention before starting more.");
                var card = s.Learning.Values.Where(c => c.SourceRecordingId is null && c.DueAt <= now).OrderBy(c => c.DueAt).ThenBy(c => c.Id).FirstOrDefault();
                if (card is null) return null;
                var recall = new VoiceRecall(startId, sessionId, card.Id, Fingerprint(card), "Question", sourceId, now, now);
                s.VoiceRecalls.Add(recall.Id, recall); return View(s, recall);
            }
            if (action == "stop")
            {
                if (active is null) return null;
                var stopped = active with { Status = "Cancelled", UpdatedAt = now }; s.VoiceRecalls[stopped.Id] = stopped; return View(s, stopped);
            }
            Guard.Require(active is not null, 409, "Start a due-card review first.");
            var item = Card(s, active);
            if (action == "reveal")
            {
                if (active.Status == "Rating" && active.AnswerUtteranceId == sourceId) return View(s, active);
                Guard.Require(active.Status == "Question" && sourceId != active.StartUtteranceId && source!.At >= active.CreatedAt,
                    409, "Wait for the user to attempt the question before revealing its reference.");
                var revealed = active with { Status = "Rating", AnswerUtteranceId = sourceId, UpdatedAt = now };
                s.VoiceRecalls[revealed.Id] = revealed; return View(s, revealed);
            }
            Guard.Require(active.Status == "Rating" && sourceId != active.AnswerUtteranceId && source!.At >= active.UpdatedAt,
                409, "Reveal the reference, then ask the user for Again, Hard, Good or Easy.");
            var spoken = Regex.Match(source!.Text.Trim(), @"^(?:(?:I (?:would )?rate (?:that|it)(?: as)?|rate (?:that|it)|that was)\s+)?(again|hard|good|easy)[.!?]?$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            Guard.Require(spoken.Success && Enum.TryParse<ReviewRating>(spoken.Groups[1].Value, true, out var parsed) && parsed.ToString() == rating,
                400, "Ask for one explicit rating: Again, Hard, Good or Easy. Do not infer a rating from conversation.");
            var chosen = Enum.Parse<ReviewRating>(rating);
            LearningService.Apply(s, item.Id, new(VoicePracticeService.Operation("recall-rate", sessionId, sourceId), chosen, 0), now, "not_measured");
            var complete = active with { Status = "Completed", RatingUtteranceId = sourceId, Rating = chosen, UpdatedAt = now };
            s.VoiceRecalls[complete.Id] = complete; return View(s, complete);
        }, ct);
    }
    private static LearningItem Card(RuntimeState s, VoiceRecall recall)
    {
        Guard.Require(s.Learning.TryGetValue(recall.CardId, out var card) && Fingerprint(card) == recall.CardFingerprint,
            409, "This card changed or was reviewed elsewhere. Stop this recall and start a fresh one.");
        return card;
    }
    private static VoiceRecallView View(RuntimeState s, VoiceRecall recall)
    {
        var finished = recall.Status is "Completed" or "Cancelled";
        var card = finished ? s.Learning.GetValueOrDefault(recall.CardId) : Card(s, recall);
        var due = recall.RatingUtteranceId is Guid rated ? s.Reviews.GetValueOrDefault(VoicePracticeService.Operation("recall-rate", recall.SessionId, rated))?.Result.DueAt : null;
        return new(recall.Id, recall.CardId, recall.Status, card?.Domain.ToString(), finished ? null : card?.Prompt,
            recall.Status == "Rating" ? card?.Answer : null, recall.Rating?.ToString(), due, "not_measured",
            card?.GeneratedQuestionId is not null ? VoiceRecallView.GeneratedOrigin : null);
    }
}
public sealed record VoiceRecallView(Guid RecallId, Guid CardId, string Status, string? Domain, string? Prompt,
    string? Reference, string? Rating, DateTimeOffset? NextDueAt, string TimingSource, string? Origin = null)
{
    // Trusted origin of an AI-generated card: its reference is AI-written and not verified.
    public const string GeneratedOrigin = "ai-generated-unverified";
}
