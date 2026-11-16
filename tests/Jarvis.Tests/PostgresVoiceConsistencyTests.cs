using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Jarvis.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task Device_grant_audits_migrate_and_survive_replay_and_restart_until_erasure()
    {
        await using var database = await TestDatabase.CreateAsync(); const string deviceId = "persistent-grant-device";
        var now = DateTimeOffset.UtcNow;
        var legacyId = Guid.NewGuid();
        await using (var legacy = new SessionDbContext(new DbContextOptionsBuilder<SessionDbContext>()
            .UseNpgsql(database.ConnectionString).Options))
        {
            await legacy.Database.GetService<IMigrator>().MigrateAsync("20261116061743_DeviceGrants");
            const string emptyConstraints = "{}";
            await legacy.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "DeviceGrants" ("Id", "DeviceId", "CapabilityId", "CapabilityVersion", "ConstraintsJson",
                    "CredentialHash", "GrantedAt", "ExpiresAt")
                VALUES ({legacyId}, 'legacy-fixture', 'homeassistant.turn', '1.0.0', CAST({emptyConstraints} AS jsonb),
                    'old-hash', {now}, {now.AddDays(1)})
                """);
        }
        var request = DeviceGrantTests.GrantRequest();
        Guid grantId;
        using (var host = new PostgresApiFactory(database.ConnectionString))
        {
            await host.Services.GetRequiredService<DeviceService>().RegisterAsync(new(deviceId, "Grant fixture", DeviceKind.AndroidCompanion,
                new(true, true, false, false, [])));
            using var owner = host.AuthorisedClient();
            var credentialResponse = await owner.PostAsJsonAsync($"/api/devices/{deviceId}/credential", new { }); credentialResponse.EnsureSuccessStatusCode();
            var route = $"/api/devices/{deviceId}/grants";
            var first = owner.PostAsJsonAsync(route, request);
            var retry = owner.PostAsJsonAsync(route, request with { ConstraintsJson = """{ "allowedEntityIds": [ "light.kitchen" ] }""" });
            using var issued = await first;
            using var replayed = await retry;
            issued.EnsureSuccessStatusCode();
            replayed.EnsureSuccessStatusCode();
            grantId = (await issued.Content.ReadFromJsonAsync<DeviceCapabilityGrantView>(Json))!.Id;
            Assert.Equal(grantId, (await replayed.Content.ReadFromJsonAsync<DeviceCapabilityGrantView>(Json))!.Id);
            Assert.Equal(request.RequestId, grantId);
        }
        using var restarted = new PostgresApiFactory(database.ConnectionString);
        var grants = restarted.Services.GetRequiredService<IDeviceGrantStore>();
        Assert.Equal(new DeviceGrantAudit(request.RequestId, "fixture-owner", request.Reason!), (await grants.GetAsync(grantId))!.Audit);
        var legacyGrant = await grants.GetAsync(legacyId);
        Assert.NotNull(legacyGrant);
        Assert.Null(legacyGrant.Audit);
        Assert.Null(await grants.FindActiveAsync("legacy-fixture", "homeassistant.turn", "1.0.0", "old-hash", now));
        var revokedAt = DateTimeOffset.UtcNow;
        Assert.True(await grants.RevokeAsync(grantId, revokedAt));
        Assert.Equal(0, await grants.PruneAsync(revokedAt));
        using var ownerAfterRestart = restarted.AuthorisedClient();
        using var revoked = await ownerAfterRestart.PostAsJsonAsync($"/api/devices/{deviceId}/grants", request);
        revoked.EnsureSuccessStatusCode();
        var replay = (await revoked.Content.ReadFromJsonAsync<DeviceCapabilityGrantView>(Json))!;
        Assert.False(replay.Effective);
        Assert.NotNull(replay.RevokedAt);
        var privacy = restarted.Services.GetRequiredService<IPrivacyStore>(); var status = await privacy.StatusAsync();
        var erased = await privacy.EraseAsync(new(Guid.NewGuid(), status.Revision, PrivacyData.ErasePhrase),
            PrivacyData.Hash("fixture-owner-key"), now, default);
        Assert.True(erased.Erased);
        Assert.Equal(2, (await privacy.JobsAsync()).Single().Removed.DeviceGrants);
        await using var db = await restarted.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>().CreateDbContextAsync();
        Assert.Empty(await db.DeviceGrants.ToArrayAsync());
    }

    [PostgresFact]
    public async Task Narrow_paid_pause_read_returns_only_the_effective_pause_state()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var host = new PostgresApiFactory(database.ConnectionString);
        var runtime = host.Services.GetRequiredService<IRuntimeStore>();
        await runtime.UpdateAsync(state => { state.AgentControls = new(Paused: true); return true; });
        Assert.True(await runtime.ReadPaidAiPausedAsync());
        await runtime.UpdateAsync(state => { state.AgentControls = new(PaidAiPaused: false, AgentsPaused: true); return true; });
        Assert.False(await runtime.ReadPaidAiPausedAsync());
    }

    [PostgresFact]
    public async Task Voice_projection_repair_survives_restart_is_idempotent_and_respects_erasure()
    {
        await using var database = await TestDatabase.CreateAsync(); var sessionId = Guid.NewGuid(); var turnId = Guid.NewGuid();
        using (var first = new PostgresApiFactory(database.ConnectionString)) {
            var sessions = first.Services.GetRequiredService<ISessionStore>(); var now = DateTimeOffset.UtcNow;
            await sessions.AddAsync(new(sessionId, ActivityType.General, "Crash recovery", SessionStatus.Active, "phone", null, now, now, []));
            var failing = new VoiceTranscriptService(sessions, new FailVoiceProjection(first.Services.GetRequiredService<IRuntimeStore>()), TimeProvider.System);
            await Assert.ThrowsAsync<IOException>(() => failing.RecordAsync(sessionId, turnId, "user", "Durable captured speech", 1.25, default));
        }
        using var restarted = new PostgresApiFactory(database.ConnectionString); var repair = restarted.Services.GetRequiredService<IVoiceRepairStore>();
        var counts = await Task.WhenAll(repair.RepairTranscriptsAsync(default), repair.RepairTranscriptsAsync(default)); Assert.Equal(1, counts.Sum());
        Assert.Equal(0, await repair.RepairTranscriptsAsync(default));
        var runtime = restarted.Services.GetRequiredService<IRuntimeStore>(); var turn = Assert.Single(await runtime.ReadAsync(s => s.VoiceTurns.Values.ToArray()));
        Assert.Equal(turnId, turn.Id); Assert.Equal(1.25, turn.SpeechSeconds); Assert.Equal("Durable captured speech", turn.Text);
        Assert.Empty((await restarted.Services.GetRequiredService<ISessionStore>().GetAsync(sessionId))!.PendingVoiceProjectionIds!);
        // Idle repair has no runtime write (xmin is unchanged).
        await using var db = await restarted.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>().CreateDbContextAsync();
        var before = await db.Database.SqlQueryRaw<string>("SELECT xmin::text AS \"Value\" FROM \"RuntimeDocuments\" WHERE \"Id\"=1").SingleAsync();
        await repair.RepairTranscriptsAsync(default);
        Assert.Equal(before, await db.Database.SqlQueryRaw<string>("SELECT xmin::text AS \"Value\" FROM \"RuntimeDocuments\" WHERE \"Id\"=1").SingleAsync());
        var privacy = restarted.Services.GetRequiredService<IPrivacyStore>(); var status = await privacy.StatusAsync();
        await privacy.EraseAsync(new(Guid.NewGuid(), status.Revision, PrivacyData.ErasePhrase), PrivacyData.Hash("fixture-key"), DateTimeOffset.UtcNow);
        using var epoch = PrivacyContext.Enter(status.Epoch);
        await Assert.ThrowsAsync<DomainException>(() => repair.RepairTranscriptsAsync(default));
        Assert.Empty(await db.Sessions.ToArrayAsync());
    }
}
