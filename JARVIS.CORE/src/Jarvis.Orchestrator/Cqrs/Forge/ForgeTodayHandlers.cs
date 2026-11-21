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

public sealed record GetForgeWeek : IQuery<ForgeWeek>;

/// <summary>Owner's weekly summary (J4): counts of existing records per division; unknowns stay null.</summary>
public sealed class ForgeWeekQueries(ForgeTodayService today, IForgeAvailability feature, TimeProvider clock) :
    IQueryHandler<GetForgeWeek, ForgeWeek>
{
    public async Task<ForgeWeek> HandleAsync(GetForgeWeek query, CancellationToken ct = default)
    {
        ForgeProgrammePolicy.RequireEnabled(feature);
        var week = await today.BuildWeekAsync(clock.GetUtcNow(), ct);
        Guard.Require(week is not null, 404, "Create a Forge programme first.");
        return week!;
    }
}
