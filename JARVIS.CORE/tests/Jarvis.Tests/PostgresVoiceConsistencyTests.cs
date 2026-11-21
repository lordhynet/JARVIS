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
            await PostgresSchemaCompatibility.MigrateAsync(legacy);
        }
        var request = DeviceGrantTests.GrantRequest();
        Guid grantId;
        string originalSecret;
        using (var host = new PostgresApiFactory(database.ConnectionString))
        {
            await host.Services.GetRequiredService<DeviceService>().RegisterAsync(new(deviceId, "Grant fixture", DeviceKind.AndroidCompanion,
                new(true, true, false, false, [])));
            using var owner = host.AuthorisedClient();
            var credentialResponse = await owner.PostAsJsonAsync($"/api/devices/{deviceId}/credential", new { }); credentialResponse.EnsureSuccessStatusCode();
            originalSecret = (await credentialResponse.Content.ReadFromJsonAsync<IssuedDeviceCredential>(Json))!.Secret;
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
        var failure = new FailBeforeCoreCommit(restarted.Services.GetRequiredService<ICoreTransaction>());
        var failingCredentials = new DeviceCredentials(restarted.Services.GetRequiredService<IRuntimeReader>(), failure, TimeProvider.System);
        await Assert.ThrowsAsync<IOException>(() => failingCredentials.IssueAsync(deviceId, default));
        Assert.NotNull(await restarted.Services.GetRequiredService<DeviceCredentials>().AuthenticateAsync(originalSecret, default));
        Assert.Null((await grants.GetAsync(grantId))!.RevokedAt);
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
    public async Task Concurrent_grant_creation_and_credential_rotation_leave_a_current_or_revoked_binding()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var host = new PostgresApiFactory(database.ConnectionString);
        const string deviceId = "concurrent-grant-device";
        await host.Services.GetRequiredService<DeviceService>().RegisterAsync(new(deviceId, "Grant fixture",
            DeviceKind.AndroidCompanion, new(true, true, false, false, [])));
        var credentials = host.Services.GetRequiredService<DeviceCredentials>();
        var previous = await credentials.IssueAsync(deviceId, default);
        var creation = host.Services.GetRequiredService<DeviceGrantService>().GrantAsync(deviceId, DeviceGrantTests.GrantRequest(),
            new("fixture-owner", [DeviceGrantService.ManageScope]));
        var rotation = credentials.IssueAsync(deviceId, default);
        await Task.WhenAll(creation, rotation);

        var view = await creation;
        var credential = await rotation;
        var saved = (await host.Services.GetRequiredService<IDeviceGrantStore>().GetAsync(view.Id))!;
        if (saved.CredentialHash == PrivacyData.Hash(credential.Secret))
            Assert.Null(saved.RevokedAt);
        else
        {
            Assert.Equal(PrivacyData.Hash(previous.Secret), saved.CredentialHash);
            Assert.NotNull(saved.RevokedAt);
        }
        Assert.Null(await credentials.AuthenticateAsync(previous.Secret, default));
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
    public async Task Failed_atomic_voice_and_session_writes_roll_back_in_postgres()
    {
        await using var database = await TestDatabase.CreateAsync();
        var clock = new VoiceFixtureClock();
        using var host = new PostgresApiFactory(database.ConnectionString, clock);
        var sessions = host.Services.GetRequiredService<ISessionStore>();
        var runtime = host.Services.GetRequiredService<IRuntimeStore>();
        var sessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var now = clock.GetUtcNow();
        await sessions.AddAsync(new(sessionId, ActivityType.General, "Crash recovery", SessionStatus.Active, "phone", null, now, now, []));
        var failure = new FailBeforeCoreCommit(host.Services.GetRequiredService<ICoreTransaction>());
        var failing = new VoiceTranscriptService(failure, clock);
        await Assert.ThrowsAsync<IOException>(() => failing.RecordAsync(sessionId, turnId, "user", "Captured speech", 1.25, default));
        Assert.Empty((await sessions.GetAsync(sessionId))!.Messages!);
        Assert.Empty(await runtime.ReadAsync(s => s.VoiceTurns));

        var service = host.Services.GetRequiredService<VoiceTranscriptService>();
        await service.RecordAsync(sessionId, turnId, "user", "Captured speech", 1.25, default);
        await service.RecordAsync(sessionId, turnId, "user", "Captured speech", 1.25, default);
        Assert.Equal(turnId, Assert.Single((await sessions.GetAsync(sessionId))!.Messages!).RequestId);
        Assert.Equal(turnId, Assert.Single(await runtime.ReadAsync(s => s.VoiceTurns.Values.ToArray())).Id);
        Assert.Empty((await sessions.GetAsync(sessionId))!.PendingVoiceProjectionIds!);
        await VoiceConsistencyTests.VerifySessionAndLeaseRollbackAsync(host.Services);
    }

    [PostgresFact]
    public async Task Migration_recovers_pending_transcripts_and_reconciles_stale_lease_and_grants()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sessionId = Guid.NewGuid();
        var retiredId = Guid.NewGuid();
        var missingId = Guid.NewGuid();
        var existingId = Guid.NewGuid();
        var retiredTurnId = Guid.NewGuid();
        var grantId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var orphan = new AudioLease(sessionId, "old-device", 19, now.AddMinutes(2));
        await using (var before = new SessionDbContext(new DbContextOptionsBuilder<SessionDbContext>()
            .UseNpgsql(database.ConnectionString).Options))
        {
            await before.Database.GetService<IMigrator>().MigrateAsync("20261116101533_DeviceGrantAudit");
            await InsertLegacySession(new(sessionId, ActivityType.General, "Pending", SessionStatus.Paused,
                null, null, now, now, [], Messages: [
                    new(missingId, "user", "Recovered speech", now, true, 1.25),
                    new(existingId, "assistant", "Already projected", now, true),
                    new(Guid.NewGuid(), "user", "Ordinary text", now)
                ], PendingVoiceProjectionIds: [missingId, existingId]));
            await InsertLegacySession(new(retiredId, ActivityType.General, "Retired", SessionStatus.Ended,
                null, null, now, now, [], Messages: [new(retiredTurnId, "user", "Retired speech", now, true)],
                PendingVoiceProjectionIds: [retiredTurnId]));

            // Seed the legacy schema without asking the current EF model to insert new columns.
            Task<int> InsertLegacySession(ConversationSession session) => before.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "Sessions" ("Id", "Activity", "Title", "Status", "CreatedAt", "UpdatedAt", "MessagesJson", "PendingVoiceProjectionIdsJson")
                VALUES ({session.Id}, {(int)session.Activity}, {session.Title}, {(int)session.Status}, {session.CreatedAt}, {session.UpdatedAt},
                    {System.Text.Json.JsonSerializer.Serialize(session.Messages)}, CAST({System.Text.Json.JsonSerializer.Serialize(session.PendingVoiceProjectionIds)} AS jsonb))
                """);
            var oldState = new RuntimeState { AudioLease = orphan };
            oldState.VoiceTurns[existingId] = new(existingId, sessionId, "assistant", "Original projection", null, now);
            oldState.RetiredSessions.Add(retiredId);
            oldState.DeviceCredentials["old-device"] = new("old-device", "current-hash", now);
            var legacyRuntimeJson = System.Text.Json.JsonSerializer.Serialize(oldState);
            await before.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"RuntimeDocuments\" SET \"Json\"=CAST({legacyRuntimeJson} AS jsonb) WHERE \"Id\"=1");
            const string constraints = "{}";
            await before.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "DeviceGrants" ("Id", "DeviceId", "CapabilityId", "CapabilityVersion", "ConstraintsJson",
                    "CredentialHash", "GrantedAt", "ExpiresAt", "GrantedBy", "Reason")
                VALUES ({grantId}, 'old-device', 'homeassistant.turn', '1.0.0', CAST({constraints} AS jsonb),
                    'previous-hash', {now}, {now.AddDays(1)}, 'fixture-owner', 'Stale grant')
                """);
            await before.SaveChangesAsync();
            await PostgresSchemaCompatibility.MigrateAsync(before);
        }

        using var host = new PostgresApiFactory(database.ConnectionString);
        var state = await host.Services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s);
        Assert.Equal(2, state.VoiceTurns.Count);
        Assert.Equal("Recovered speech", state.VoiceTurns[missingId].Text);
        Assert.Equal(1.25, state.VoiceTurns[missingId].SpeechSeconds);
        Assert.Equal("Original projection", state.VoiceTurns[existingId].Text);
        Assert.Null(state.AudioLease);
        Assert.Equal(orphan, state.AudioDrainLease);
        Assert.Equal(orphan.ExpiresAt, state.AudioDrainUntil);
        var sessions = host.Services.GetRequiredService<ISessionStore>();
        Assert.Empty((await sessions.GetAsync(sessionId))!.PendingVoiceProjectionIds!);
        Assert.Empty((await sessions.GetAsync(retiredId))!.PendingVoiceProjectionIds!);
        var migratedGrant = (await host.Services.GetRequiredService<IDeviceGrantStore>().GetAsync(grantId))!;
        Assert.NotNull(migratedGrant.RevokedAt);
        Assert.False(migratedGrant.PreApproved);
        Assert.False(migratedGrant.AllowWhenLocked);
    }
}
