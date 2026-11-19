using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Workflows;

// Deterministic retrieval only. No model, invented CV facts, tool execution or provider spend.
public sealed class PracticePlanProcessor(IRuntimeReader reader, TimeProvider clock)
{
    public Task<PracticePlan> BuildAsync(CancellationToken ct = default) => reader.ReadAsync(state =>
    {
        var now = clock.GetUtcNow();
        var steps = state.Learning.Values.Where(x => x.SourceRecordingId is null && x.Domain != LearningDomain.French && x.DueAt <= now)
            .OrderBy(x => x.DueAt).ThenByDescending(x => x.Difficulty).ThenBy(x => x.Id)
            .Take(2).Select(x => new PracticeStep("learning", x.Id, "Recall the answer to your saved learning card.")).ToList();
        var story = state.Stories.Values.OrderBy(x => x.UpdatedAt).ThenBy(x => x.Id).FirstOrDefault();
        if (story is not null) steps.Add(new("story", story.Id, "Retell this saved story: frame, problem, personal ownership, action, outcome, then stop."));
        var gaps = new List<string>();
        if (steps.All(x => x.Kind != "learning")) gaps.Add("No interview, technical or communication cards are due.");
        if (story is null) gaps.Add("Add a real project story to practise personal ownership.");
        return new PracticePlan(steps, gaps);
    }, ct);
}
