using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Travel;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task Travel_simulation_receipts_survive_restart_without_duplicate_confirmation_or_notifications()
    {
        await using var database = await TestDatabase.CreateAsync(); var clock = new TravelClock(); var request = new TravelSimulationRequest(Guid.NewGuid(), "jubilee", "severe");
        using var invocation = CoreInvocationContext.Enter(InvocationOrigin.ManualClient);
        using (var first = new PostgresApiFactory(database.ConnectionString, clock)) {
            var store = first.Services.GetRequiredService<ITravelStore>(); await store.UpdateAsync((s, _) => { var route = TravelRig.Route(); s.Routes[route.Id] = route; return true; });
            Assert.Empty((await first.Services.GetRequiredService<TravelMonitor>().SimulateAsync(request)).NotificationIds);
        }
        using var second = new PostgresApiFactory(database.ConnectionString, clock); var monitor = second.Services.GetRequiredService<TravelMonitor>();
        Assert.True((await monitor.SimulateAsync(request)).Replayed);
        Assert.Equal(1, await second.Services.GetRequiredService<ITravelStore>().ReadAsync(s => s.Lines["jubilee"].CandidateCount));
        var confirm = request with { RequestId = Guid.NewGuid() }; var result = await monitor.SimulateAsync(confirm); Assert.Single(result.NotificationIds);
        Assert.True((await monitor.SimulateAsync(confirm)).Replayed);
        Assert.Single(await second.Services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.AgentOutbox.Values.ToArray()));
    }
}
