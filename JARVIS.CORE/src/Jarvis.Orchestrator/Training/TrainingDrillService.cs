using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;

namespace Jarvis.Orchestrator.Training;

public sealed class TrainingDrillService(IRuntimeStore store, ISessionReader sessions, TimeProvider clock)
{
    public async Task<TrainingDrillView> StartAsync(StartTrainingDrillRequest request, CancellationToken ct)
    {
        Guard.Require(request.RequestId != Guid.Empty && Enum.IsDefined(request.Mode), 400, "Request identity and supported drill mode are required.");
        var replay = await store.ReadAsync(s => s.TrainingDrills.TryGetValue(request.RequestId, out var old) ? View(s, old) : null, ct);
        if (replay is not null)
        {
            Guard.Require(replay.Drill.SessionId == request.SessionId && replay.Drill.Mode == request.Mode && replay.Drill.RequestedQuestionId == request.QuestionId,
                409, "Drill request ID was reused for different input."); return replay;
        }
        Guard.Require(await sessions.GetAsync(request.SessionId, ct) is { Status: SessionStatus.Active }, 409, "Open an active session first.");
        return await store.UpdateAsync(s =>
        {
            Guard.Require(!s.RetiredSessions.Contains(request.SessionId), 409, "This session was retired by retention.");
            if (s.TrainingDrills.TryGetValue(request.RequestId, out var previous))
            {
                Guard.Require(previous.SessionId == request.SessionId && previous.Mode == request.Mode && previous.RequestedQuestionId == request.QuestionId, 409, "Drill request ID was reused for different input.");
                return View(s, previous);
            }
            Guard.Require(s.TrainingDrills.Count < 1000, 409, "Drill history is full.");
            var candidates = QuestionBank.All.Where(q => DrillCatalog.Supports(request.Mode, q)).ToList();
            if (request.Mode == TrainingMode.SpacedReview) candidates = candidates.Where(q => s.Learning.TryGetValue(q.Id, out var card) && card.DueAt <= clock.GetUtcNow()).ToList();
            if (request.QuestionId is Guid chosen)
            {
                Guard.Require(request.Mode != TrainingMode.RandomMixedInterview, 400, "Mixed interviews choose multiple domains; do not pin a question.");
                candidates = candidates.Where(q => q.Id == chosen).ToList();
            }
            Guard.Require(candidates.Count > 0, 409, request.Mode == TrainingMode.SpacedReview ? "No matching technical questions are due. Use another mode for new practice." : "No authored scenario supports this question and mode.");
            double Priority(TrainingQuestion q) => s.Learning.TryGetValue(q.Id, out var item) && item.MasteryScore is double score && score < 3 ? score - 10 :
                s.Learning.TryGetValue(q.Id, out item) && item.DueAt <= clock.GetUtcNow() ? -1 : !s.TrainingAttempts.Values.Any(a => a.QuestionId == q.Id && a.DeletedAt is null && !a.Skipped) ? 0 : 1;
            var ordered = candidates.OrderBy(Priority).ThenBy(q => s.Learning.GetValueOrDefault(q.Id)?.DueAt ?? DateTimeOffset.MinValue).ThenBy(q => q.Id).ToList();
            if (request.Mode == TrainingMode.RandomMixedInterview)
            {
                var shuffled = ordered.ToArray(); Random.Shared.Shuffle(shuffled);
                ordered = shuffled.GroupBy(x => x.Domain).Select(g => g.OrderBy(Priority).First()).OrderBy(Priority).Take(3).ToList();
            }
            else ordered = ordered.Take(1).ToList();
            var drill = new TrainingDrill(request.RequestId, request.SessionId, request.Mode, request.QuestionId, 1, "Active", clock.GetUtcNow(), DrillCatalog.Build(request.Mode, ordered), []);
            s.TrainingDrills.Add(drill.Id, drill); return View(s, drill);
        }, ct);
    }
    public Task<TrainingDrillView> CancelAsync(Guid id, int expectedRevision, CancellationToken ct) => store.UpdateAsync(s =>
    {
        Guard.Require(s.TrainingDrills.TryGetValue(id, out var drill), 404, "Drill not found.");
        if (drill.Status == "Cancelled") return View(s, drill);
        Guard.Require(drill.Revision == expectedRevision && drill.Status == "Active", 409, "Drill changed or is already finished.");
        var updated = drill with { Status = "Cancelled", Revision = drill.Revision + 1 }; s.TrainingDrills[id] = updated; return View(s, updated);
    }, ct);
    public static TrainingDrillView View(RuntimeState state, TrainingDrill drill) => new(drill, drill.AttemptIds.Select(id => state.TrainingAttempts[id]).Where(x => x.DeletedAt is null).ToList());
}
