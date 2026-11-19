using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Microsoft.Extensions.DependencyInjection;
namespace Jarvis.Tests;
public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task Native_handoff_recovers_after_restart_in_target_ready_without_losing_the_checkpoint()
    {
        await using var database = await TestDatabase.CreateAsync(); Guid sessionId, handoffId; var clock = new TravelClock();
        using (var first = new PostgresApiFactory(database.ConnectionString, clock)) {
            var s = first.Services; await NativeHandoffTests.Register(s, "laptop", DeviceKind.WindowsAgent); await NativeHandoffTests.Register(s, "phone", DeviceKind.AndroidCompanion);
            var session = await s.GetRequiredService<SessionService>().CreateAsync(new(ActivityType.Communication, "Private discussion", "laptop")); sessionId = session.Id;
            var control = s.GetRequiredService<CompanionHandoffService>(); var offer = Assert.Single((await control.ControlAsync(new("laptop", "start", session.Id, TargetDeviceId: "phone", RequestId: Guid.NewGuid(), Locked: false))).Offers); handoffId = offer.HandoffId;
            await control.ControlAsync(new("phone", "ready", session.Id, handoffId, Locked: false));
        }
        using var restarted = new PostgresApiFactory(database.ConnectionString, clock); var services = restarted.Services; var handoffs = services.GetRequiredService<CompanionHandoffService>();
        Assert.Equal(HandoffState.TargetReady, Assert.Single((await handoffs.ControlAsync(new("phone", Locked: false))).Offers).State);
        await handoffs.ControlAsync(new("laptop", "release", sessionId, handoffId, AudioStopped: true));
        var prepared = await services.GetRequiredService<CompanionVoiceService>().PrepareAsync(new(Guid.NewGuid(), "phone", true, false), default);
        Assert.Equal(sessionId, prepared.SessionId); var sessionAfter = (await services.GetRequiredService<ISessionReader>().GetAsync(sessionId))!;
        Assert.Equal(HandoffState.Completed, sessionAfter.Handoff!.State); Assert.Contains(sessionAfter.Checkpoints, c => c.Summary.Contains("Continue the same conversation"));
    }
}
