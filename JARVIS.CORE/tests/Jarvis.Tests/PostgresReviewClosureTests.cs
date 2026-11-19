using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Jarvis.Persistence;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task Idle_runtime_updates_preserve_row_version_and_narrow_audio_projections_match_state()
    {
        await using var database = await TestDatabase.CreateAsync();
        var clock = new VoiceFixtureClock();
        using var host = new PostgresApiFactory(database.ConnectionString, clock);
        using var client = host.AuthorisedClient(); var state = host.Services.GetRequiredService<IRuntimeStore>();
        var factory = host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>();
        async Task<string> Version() { await using var db = await factory.CreateDbContextAsync(); return await db.Database.SqlQueryRaw<string>("SELECT xmin::text AS \"Value\" FROM \"RuntimeDocuments\" WHERE \"Id\"=1").SingleAsync(); }
        var before = await Version();
        for (var i = 0; i < 10; i++) await state.UpdateAsync(_ => false);
        Assert.Equal(before, await Version());
        var now = clock.GetUtcNow();
        var session = await CreateSession(client);
        var lease = new AudioLease(session.Id, "fixture", 12345, now.AddMinutes(1));
        var sessions = host.Services.GetRequiredService<ISessionStore>();
        session = await sessions.UpdateAsync(session.Id, current => current with
        {
            ActiveDeviceId = "fixture",
            Messages = [new(Guid.NewGuid(), "user", "Synthetic transcript with [brackets].", now)]
        })
            ?? throw new InvalidOperationException("The test session disappeared during update.");
        Assert.True((await state.ReadAudioLeaseStateAsync()).IsAvailable(now));
        Assert.Null(await state.ReadRegisteredDeviceAsync("fixture"));
        var devices = host.Services.GetRequiredService<DeviceService>();
        await devices.RegisterAsync(new("fixture", "Fixture companion", DeviceKind.AndroidCompanion, new(true, true, false, false, [])));
        await devices.VerifyAsync("fixture");
        Assert.Equal("fixture", (await devices.RequireOnlineAsync("fixture", true, default)).Registration.DeviceId);
        await sessions.AddAsync(new(Guid.NewGuid(), ActivityType.General, "unrelated", SessionStatus.Active,
            "other-device", null, now, now, []));
        await state.UpdateAsync(s => { s.AudioLease = lease; return true; });
        Assert.Equal(lease, await state.ReadAudioLeaseAsync());
        var active = await state.ReadAudioLeaseStateAsync();
        Assert.Equal(lease, active.Lease);
        Assert.False(active.IsAvailable(now));
        Assert.True(active.IsAvailable(lease.ExpiresAt));
        using var fence = new LiveAudioFence(lease, host.Services.GetRequiredService<AudioOwnership>(),
            host.Services.GetRequiredService<AudioFenceChanges>(), clock);
        Assert.True(await fence.RefreshAsync(default));
        await state.UpdateAsync(s => { AudioFencing.Revoke(s); return true; });
        Assert.False(fence.IsCurrent());
        Assert.False(await fence.RefreshAsync(default));
        var drain = await state.ReadAudioLeaseStateAsync();
        Assert.Null(drain.Lease);
        Assert.Equal(lease.ExpiresAt, drain.DrainUntil);
        Assert.False(drain.IsAvailable(now));
        Assert.True(drain.IsAvailable(lease.ExpiresAt));
        var projection = await host.Services.GetRequiredService<ISessionReader>().ReadAudioStateAsync(session.Id);
        Assert.Equal(session.Status, projection!.Status); Assert.Equal(session.ActiveDeviceId, projection.ActiveDeviceId);
        var reader = host.Services.GetRequiredService<ISessionReader>();
        var summary = Assert.Single(await reader.ListPreparationAsync("fixture", Guid.NewGuid()));
        Assert.Equal(session.Id, summary.Id); Assert.Equal(1, summary.MessageCount);
        Assert.Null(await reader.ReadHandoffAsync(session.Id));
    }
}
