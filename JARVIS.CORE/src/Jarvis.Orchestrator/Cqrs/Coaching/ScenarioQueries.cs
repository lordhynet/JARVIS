using Jarvis.Contracts;
using Jarvis.Orchestrator.Coaching;
using Jarvis.Orchestrator.Domains;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Cqrs.Coaching;

public sealed record ScenarioAvailability(CoachingScenario Scenario, bool Active, string Mode = "Everyday", string StartInstruction = "Ask JARVIS by voice to start this scenario; it asks one question and waits for each captured answer.");
public sealed record ListCoachingScenarios : IQuery<IReadOnlyList<ScenarioAvailability>>;
public sealed record GetScenarioDebriefs(Guid SessionId) : IQuery<IReadOnlyList<CoachingScenarioDebrief>>;
public sealed class ScenarioQueries(IRuntimeReader reader, IDomainRegistry domains) : IQueryHandler<ListCoachingScenarios, IReadOnlyList<ScenarioAvailability>>,
    IQueryHandler<GetScenarioDebriefs, IReadOnlyList<CoachingScenarioDebrief>>
{
    public async Task<IReadOnlyList<ScenarioAvailability>> HandleAsync(ListCoachingScenarios q, CancellationToken ct = default)
    {
        var result = new List<ScenarioAvailability>();
        foreach (var scenario in CoachingScenarios.All) {
            DomainResolved? active;
            try { active = await domains.ResolveActiveAsync(scenario.Key, ct); }
            catch (DomainException error) when (error.StatusCode is 404 or 409) { active = null; }
            result.Add(new(scenario, active is not null && active.Version.Manifest.Configuration?.GetValueOrDefault("coachingScenarioId") == scenario.Key &&
                (active.ApprovedRiskClass ?? DomainPolicy.Risk(active.Version.Manifest)) == DomainRiskClass.General));
        }
        return result;
    }
    public Task<IReadOnlyList<CoachingScenarioDebrief>> HandleAsync(GetScenarioDebriefs q, CancellationToken ct = default) => reader.ReadAsync(s =>
        (IReadOnlyList<CoachingScenarioDebrief>)s.VoiceCoachings.Values.Where(v => v.SessionId == q.SessionId && v.ScenarioKey is not null && !s.RetiredSessions.Contains(q.SessionId))
            .OrderByDescending(v => v.UpdatedAt ?? v.CreatedAt).Take(20).Select(v => {
                try { return VoiceCoachingService.Debrief(s, v); }
                catch (DomainException error) when (error.StatusCode == 409) {
                    return new CoachingScenarioDebrief(v.ScenarioKey!, CoachingScenarios.Get(v.ScenarioKey!).Title, "SourceUnavailable",
                        v.ScenarioAttempts?.Count ?? 0, [], [], [], [], "An answer or its source changed; previous scores and delivery are unavailable.");
                }
            }).ToArray(), ct);
}
