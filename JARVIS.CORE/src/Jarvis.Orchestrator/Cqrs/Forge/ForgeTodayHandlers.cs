using Jarvis.Contracts;
using Jarvis.Orchestrator.Forge;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Orchestrator.Cqrs.Forge;

public sealed record GetForgeToday : IQuery<ForgeBriefing>;

/// <summary>Owner's browser Today view: the same read-only block the spoken briefing uses (PF-09).</summary>
public sealed class ForgeTodayQueries(ForgeTodayService today, IForgeAvailability feature, TimeProvider clock) :
    IQueryHandler<GetForgeToday, ForgeBriefing>
{
    public async Task<ForgeBriefing> HandleAsync(GetForgeToday query, CancellationToken ct = default)
    {
        ForgeProgrammePolicy.RequireEnabled(feature);
        var briefing = await today.BuildAsync(clock.GetUtcNow(), ct);
        Guard.Require(briefing is not null, 404, "Create a Forge programme first.");
        return briefing!;
    }
}
