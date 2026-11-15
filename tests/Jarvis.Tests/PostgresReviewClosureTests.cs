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
        await using var database = await TestDatabase.CreateAsync(); using var host = new PostgresApiFactory(database.ConnectionString);
        using var client = host.AuthorisedClient(); var state = host.Services.GetRequiredService<IRuntimeStore>();
        var factory = host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>();
        async Task<string> Version() { await using var db = await factory.CreateDbContextAsync(); return await db.Database.SqlQueryRaw<string>("SELECT xmin::text AS \"Value\" FROM \"RuntimeDocuments\" WHERE \"Id\"=1").SingleAsync(); }
        var before = await Version();
        for (var i = 0; i < 10; i++) await state.UpdateAsync(_ => false);
        Assert.Equal(before, await Version());
        var session = await CreateSession(client); var lease = new AudioLease(session.Id, "fixture", 12345, DateTimeOffset.UtcNow.AddMinutes(1));
        await state.UpdateAsync(s => { s.AudioLease = lease; return true; });
        Assert.Equal(lease, await state.ReadAudioLeaseAsync());
        var projection = await host.Services.GetRequiredService<ISessionReader>().ReadAudioStateAsync(session.Id);
        Assert.Equal(session.Status, projection!.Status); Assert.Equal(session.ActiveDeviceId, projection.ActiveDeviceId);
    }
}
