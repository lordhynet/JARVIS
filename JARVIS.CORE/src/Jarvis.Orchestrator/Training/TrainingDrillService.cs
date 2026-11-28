using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;

namespace Jarvis.Orchestrator.Training;

public sealed class TrainingDrillService(IRuntimeStore store, ISessionReader sessions, TimeProvider clock)
{
    // Topics with authored technical QuestionBank content. Other LearningDomain values (e.g. French) are not practice topics here.
    public static IReadOnlySet<LearningDomain> Topics { get; } = QuestionBank.All.Select(q => q.Domain).ToHashSet();

    private static bool SameInput(TrainingDrill drill, StartTrainingDrillRequest request) => drill.SessionId == request.SessionId &&
        drill.Mode == request.Mode && drill.RequestedQuestionId == request.QuestionId && drill.RequestedTopic == request.Topic;

    // resumeActiveInSession: the voice path's one-active-drill policy, enforced inside the same update as creation so
    // concurrent conflicting starts cannot both create drills. A null topic resumes any active drill of the same mode
    // (legacy behaviour); an explicit topic resumes only an active drill started for that topic.
    public async Task<TrainingDrillView> StartAsync(StartTrainingDrillRequest request, CancellationToken ct, bool resumeActiveInSession = false)
    {
        Guard.Require(request.RequestId != Guid.Empty && Enum.IsDefined(request.Mode), 400, "Request identity and supported drill mode are required.");
        if (request.Topic is { } requested)
        {
            Guard.Require(Enum.IsDefined(requested) && Topics.Contains(requested), 400, "There is no authored practice for that topic yet.");
            Guard.Require(request.Mode != TrainingMode.RandomMixedInterview, 400, "A mixed interview spans several topics; choose a single-topic style instead.");
            Guard.Require(request.QuestionId is not Guid pinned || QuestionBank.All.Any(q => q.Id == pinned && q.Domain == requested), 400,
                "The chosen question does not belong to that topic.");
        }
        var replay = await store.ReadAsync(s => s.TrainingDrills.TryGetValue(request.RequestId, out var old) ? View(s, old) : null, ct);
        if (replay is not null)
        {
            Guard.Require(SameInput(replay.Drill, request), 409, "Drill request ID was reused for different input."); return replay;
        }
        Guard.Require(await sessions.GetAsync(request.SessionId, ct) is { Status: SessionStatus.Active }, 409, "Open an active session first.");
        return await store.UpdateAsync(s =>
        {
            Guard.Require(!s.RetiredSessions.Contains(request.SessionId), 409, "This session was retired by retention.");
            if (s.TrainingDrills.TryGetValue(request.RequestId, out var previous))
            {
                Guard.Require(SameInput(previous, request), 409, "Drill request ID was reused for different input.");
                return View(s, previous);
            }
            if (resumeActiveInSession && s.TrainingDrills.Values.Where(d => d.SessionId == request.SessionId && d.Status == "Active")
                    .OrderByDescending(d => d.CreatedAt).ThenBy(d => d.Id).FirstOrDefault() is { } active)
            {
                Guard.Require(active.Mode == request.Mode && (request.Topic is null || active.RequestedTopic == request.Topic), 409,
                    "A different practice is active. Ask whether to stop it before starting another.");
                return View(s, active);
            }
            Guard.Require(s.TrainingDrills.Count < 1000, 409, "Drill history is full.");
            // Topic filtering happens first, so priority, due ordering and shuffling only ever see that topic.
            var candidates = QuestionBank.All.Where(q => (request.Topic is null || q.Domain == request.Topic) && DrillCatalog.Supports(request.Mode, q)).ToList();
            if (request.Mode == TrainingMode.SpacedReview) candidates = candidates.Where(q => s.Learning.TryGetValue(q.Id, out var card) && card.DueAt <= clock.GetUtcNow()).ToList();
            if (request.QuestionId is Guid chosen)
            {
                Guard.Require(request.Mode != TrainingMode.RandomMixedInterview, 400, "Mixed interviews choose multiple domains; do not pin a question.");
                candidates = candidates.Where(q => q.Id == chosen).ToList();
            }
            Guard.Require(candidates.Count > 0, 409, request.Topic is not null
                ? request.Mode == TrainingMode.SpacedReview ? "No questions on that topic are due. Try another style for new practice on it."
                    : "That topic has no authored questions for this practice style. Try another style, such as a rapid drill."
                : request.Mode == TrainingMode.SpacedReview ? "No matching technical questions are due. Use another mode for new practice." : "No authored scenario supports this question and mode.");
            double Priority(TrainingQuestion q) => s.Learning.TryGetValue(q.Id, out var item) && item.MasteryScore is double score && score < 3 ? score - 10 :
                s.Learning.TryGetValue(q.Id, out item) && item.DueAt <= clock.GetUtcNow() ? -1 : !s.TrainingAttempts.Values.Any(a => a.QuestionId == q.Id && a.DeletedAt is null && !a.Skipped) ? 0 : 1;
            var ordered = candidates.OrderBy(Priority).ThenBy(q => s.Learning.GetValueOrDefault(q.Id)?.DueAt ?? DateTimeOffset.MinValue).ThenBy(q => q.Id).ToList();
            if (request.Mode == TrainingMode.RandomMixedInterview)
            {
                var shuffled = ordered.ToArray(); Random.Shared.Shuffle(shuffled);
                ordered = shuffled.GroupBy(x => x.Domain).Select(g => g.OrderBy(Priority).First()).OrderBy(Priority).Take(3).ToList();
            }
            else ordered = ordered.Take(1).ToList();
            var drill = new TrainingDrill(request.RequestId, request.SessionId, request.Mode, request.QuestionId, 1, "Active", clock.GetUtcNow(),
                DrillCatalog.Build(request.Mode, ordered), [], request.Topic);
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
