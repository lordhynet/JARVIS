using System.Security.Cryptography;
using System.Text;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;

namespace Jarvis.Orchestrator.Training;

// Voice reuses the canonical drill/answer services; there is no second practice history.
public sealed class VoicePracticeService(IRuntimeReader runtime, ISessionReader sessions,
    TrainingDrillService drills, TrainingService training)
{
    public static Guid Operation(string kind, Guid session, Guid utterance) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"voice-practice-v1:{kind}:{session}:{utterance}"))[..16]);

    private async Task<ConversationSession> Session(Guid id, CancellationToken ct)
    {
        var session = await sessions.GetAsync(id, ct);
        Guard.Require(session is { Status: SessionStatus.Active }, 409, "Resume the conversation before practising.");
        return session;
    }
    private static ChatMessage Utterance(ConversationSession session, Guid id)
    {
        var source = session.Messages?.LastOrDefault(m => m.Role == ChatRoles.User);
        Guard.Require(source is not null && !source.Restricted && source.RequestId == id, 409, "Use the latest unrestricted captured user utterance, not generated or historical text.");
        return source;
    }
    private Task<TrainingDrill?> Active(Guid id, CancellationToken ct) => runtime.ReadAsync(s =>
        s.TrainingDrills.Values.Where(d => d.SessionId == id && d.Status == "Active").OrderByDescending(d => d.CreatedAt).ThenBy(d => d.Id).FirstOrDefault(), ct);

    public async Task<VoicePracticeView?> CurrentAsync(Guid sessionId, CancellationToken ct)
    {
        await Session(sessionId, ct);
        var drill = await Active(sessionId, ct);
        return drill is null ? null : View(drill);
    }
    // The one-active-drill check (resume same mode/topic, otherwise refuse) runs inside the drill service's update,
    // so two concurrent starts in a session cannot both create drills.
    public async Task<VoicePracticeView> StartAsync(Guid sessionId, Guid utteranceId, TrainingMode mode, CancellationToken ct,
        LearningDomain? topic = null)
    {
        Utterance(await Session(sessionId, ct), utteranceId);
        var result = await drills.StartAsync(new(Operation("start", sessionId, utteranceId), sessionId, mode, Topic: topic), ct,
            resumeActiveInSession: true);
        return View(result.Drill);
    }
    public async Task<VoicePracticeAnswer> AnswerAsync(Guid sessionId, Guid utteranceId, CancellationToken ct)
    {
        var session = await Session(sessionId, ct);
        Guard.Require(session.Messages?.Any(m => m.RequestId == utteranceId && m.Restricted) != true, 403, "Locked-room speech cannot become a saved practice answer.");
        var id = Operation("answer", sessionId, utteranceId);
        var previous = await runtime.ReadAsync(s => s.TrainingAttempts.GetValueOrDefault(id), ct);
        if (previous is not null)
        {
            Guard.Require(previous.DeletedAt is null, 409, "That saved answer was removed; it cannot be replayed.");
            return await Result(previous, ct);
        }
        var source = Utterance(session, utteranceId);
        var drill = await Active(sessionId, ct);
        Guard.Require(drill is not null, 409, "Start a practice question before saving an answer.");
        Guard.Require(!GeneratedDrillFlow.AwaitingQuestion(drill), 409, "No practice question has been asked yet. Ask the question before saving an answer.");
        Guard.Require(drill.Id != Operation("start", sessionId, utteranceId) && source.At >= drill.CreatedAt,
            409, "The practice-start instruction is not an answer. Ask the question and wait for the user.");
        var step = drill.Steps[drill.AttemptIds.Count];
        TrainingAttempt saved;
        try { saved = await training.SubmitAsync(new(id, sessionId, step.QuestionId, step.QuestionVersion,
            drill.Mode, source.Text, DrillId: drill.Id, StepIndex: step.Index), ct); }
        catch (DomainException e) when (e.StatusCode == 409)
        {
            // Another host may have saved this utterance and advanced the stage after our read.
            var replay = await runtime.ReadAsync(s => s.TrainingAttempts.GetValueOrDefault(id), ct);
            if (replay is null || replay.DeletedAt is not null) throw;
            saved = replay;
        }
        return await Result(saved, ct);
    }
    public async Task<VoicePracticeView> CancelAsync(Guid sessionId, Guid utteranceId, CancellationToken ct)
    {
        Utterance(await Session(sessionId, ct), utteranceId);
        var active = await Active(sessionId, ct);
        Guard.Require(active is not null, 409, "No practice drill is active.");
        return View((await drills.CancelAsync(active.Id, active.Revision, ct)).Drill);
    }
    private Task<VoicePracticeAnswer> Result(TrainingAttempt answer, CancellationToken ct) => runtime.ReadAsync(s =>
    {
        var drill = s.TrainingDrills[answer.DrillId!.Value];
        var step = drill.Steps[answer.StepIndex!.Value];
        return new VoicePracticeAnswer(answer.Id, answer.CurrentAnswer, step.Reference, step.SourceUrl, View(drill));
    }, ct);
    // A generated drill waiting for its next question has no current step: it reports the outline entry it is waiting on
    // instead of indexing past the end. Generated steps are labelled so their AI-written reference is never passed as authored.
    private static VoicePracticeView View(TrainingDrill drill)
    {
        var awaiting = GeneratedDrillFlow.AwaitingQuestion(drill);
        var step = drill.Status == "Active" && !awaiting && drill.AttemptIds.Count < drill.Steps.Count ? drill.Steps[drill.AttemptIds.Count] : null;
        return new(drill.Id, drill.Mode.ToString(), drill.Status, drill.AttemptIds.Count, drill.Steps.Count,
            step?.Title, step?.Prompt, step?.CodeSnippet, step?.SuggestedSeconds, drill.RequestedTopic?.ToString(),
            awaiting ? drill.Generated!.Pending!.OutlineKey : null, step?.GeneratedQuestionId is not null);
    }
}
public sealed record VoicePracticeView(Guid DrillId, string Mode, string Status, int Answered, int Total,
    string? Title, string? Prompt, string? CodeSnippet, int? SuggestedSeconds, string? Topic = null,
    string? AwaitingOutline = null, bool AiGenerated = false);
public sealed record VoicePracticeAnswer(Guid AnswerId, string CapturedAnswer, string Reference, string SourceUrl, VoicePracticeView Progress);
