using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    private const string BeforeHotRuntime = "20261117225515_LockedVoiceHomeGrants";
    [PostgresFact]
    public async Task Hot_runtime_backfill_and_downgrade_preserve_voice_hashes_and_audio_authority()
    {
        await using var database = await TestDatabase.CreateAsync(); var id = Guid.NewGuid(); var session = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 11, 18, 10, 11, 12, TimeSpan.FromHours(1)).AddTicks(7);
        var turn = new VoiceTurn(id, session, ChatRoles.User, "Legacy source with exact timing", 3, at, Timing: new(3, 1, "core-pcm-quiet-v1"));
        var lease = new AudioLease(session, "fixture", 41, at.AddMinutes(1));
        var legacy = new RuntimeState { AudioLease = lease, AudioDrainLease = lease with { Fence = 40 }, AudioDrainUntil = at.AddMinutes(2), NextAudioFence = 42 };
        legacy.VoiceTurns[id] = turn; legacy.VoiceTickets["fixture-hash"] = new("fixture-hash", lease, at.AddSeconds(15));
        legacy.AgentControls = new(PaidAiPaused: true); legacy.DeviceCredentials["fixture"] = new("fixture", new string('a', 64), at);
        legacy.WakeCandidateAttempts["fixture"] = [at];
        var options = new DbContextOptionsBuilder<SessionDbContext>().UseNpgsql(database.ConnectionString).Options;
        await using (var db = new SessionDbContext(options)) {
            await db.Database.GetService<IMigrator>().MigrateAsync(BeforeHotRuntime);
            var json = JsonSerializer.Serialize(legacy);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"RuntimeDocuments\" SET \"Json\"=CAST({json} AS jsonb) WHERE \"Id\"=1");
            await db.Database.MigrateAsync();
        }
        using (var host = new PostgresApiFactory(database.ConnectionString)) {
            var state = await host.Services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s);
            Assert.Equal(PrivacyData.Hash(JsonSerializer.Serialize(turn)), PrivacyData.Hash(JsonSerializer.Serialize(state.VoiceTurns[id])));
            Assert.Equal(lease, state.AudioLease); Assert.Equal(legacy.AudioDrainLease, state.AudioDrainLease); Assert.Equal(legacy.AudioDrainUntil, state.AudioDrainUntil); Assert.Equal(42, state.NextAudioFence);
            Assert.Equal(legacy.VoiceTickets["fixture-hash"], state.VoiceTickets["fixture-hash"]);
            Assert.True(state.AgentControls.EffectivePaidAiPaused); Assert.Equal(legacy.DeviceCredentials["fixture"], state.DeviceCredentials["fixture"]);
            Assert.Equal(at, Assert.Single(state.WakeCandidateAttempts["fixture"]));
            await using var db = new SessionDbContext(options);
            using var cold = JsonDocument.Parse((await db.RuntimeDocuments.SingleAsync()).Json);
            Assert.False(cold.RootElement.TryGetProperty("VoiceTurns", out _)); Assert.False(cold.RootElement.TryGetProperty("AudioLease", out _));
        }
        await using (var db = new SessionDbContext(options)) {
            await db.Database.GetService<IMigrator>().MigrateAsync(BeforeHotRuntime);
            var json = await db.Database.SqlQueryRaw<string>("SELECT \"Json\"::text AS \"Value\" FROM \"RuntimeDocuments\" WHERE \"Id\"=1").SingleAsync();
            var restored = JsonSerializer.Deserialize<RuntimeState>(json)!;
            Assert.Equal(turn, restored.VoiceTurns[id]); Assert.Equal(lease, restored.AudioLease); Assert.Equal(42, restored.NextAudioFence);
            Assert.True(restored.AgentControls.EffectivePaidAiPaused); Assert.Equal(legacy.DeviceCredentials["fixture"], restored.DeviceCredentials["fixture"]);
            await db.Database.MigrateAsync(); Assert.Equal(turn, (await db.RuntimeVoiceTurns.SingleAsync()).Contract());
        }
    }
    [PostgresFact]
    public async Task Hot_rows_are_dirty_tracked_and_composite_failure_rolls_everything_back()
    {
        await using var database = await TestDatabase.CreateAsync(); using var host = new PostgresApiFactory(database.ConnectionString);
        using var client = host.AuthorisedClient(); var session = await CreateSession(client);
        var runtime = host.Services.GetRequiredService<IRuntimeStore>(); var factory = host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>();
        var id = Guid.NewGuid(); var at = new DateTimeOffset(2026, 11, 18, 10, 0, 0, TimeSpan.Zero).AddTicks(7);
        await runtime.UpdateAsync(s => { s.VoiceTurns[id] = new(id, session.Id, ChatRoles.User, "Before", null, at); s.NextAudioFence = 50;
            s.Learning[id] = new(id, LearningDomain.French, "Cold prompt", "Cold answer", 2.5, 5, at.AddDays(1), at); s.NextTrainingSequence = 123; return true; });
        async Task<string> Versions() {
            await using var db = await factory.CreateDbContextAsync();
            return await db.Database.SqlQueryRaw<string>("""
                SELECT runtime.xmin::text || ':' || voice.xmin::text AS "Value"
                FROM "RuntimeDocuments" runtime CROSS JOIN "RuntimeVoiceTurns" voice WHERE runtime."Id"=1
                """).SingleAsync();
        }
        var before = await Versions(); await runtime.UpdateAsync(s => { s.NextAudioFence++; s.AgentControls = s.AgentControls with { PaidAiPaused = !s.AgentControls.EffectivePaidAiPaused }; return true; });
        Assert.Equal(before, await Versions()); await runtime.UpdateAsync(_ => false); Assert.Equal(before, await Versions());
        var transaction = host.Services.GetRequiredService<ICoreTransaction>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.ExecuteAsync<bool>(async writes => {
            writes.Runtime.VoiceTurns[id] = writes.Runtime.VoiceTurns[id] with { Text = "Must roll back" };
            writes.Runtime.NextAudioFence = 999;
            writes.Runtime.VoiceTickets["rollback"] = new("rollback", new(session.Id, "fixture", 999, at), at);
            await writes.UpdateSessionAsync(session.Id, s => s with { Title = "Must roll back" });
            throw new InvalidOperationException("Synthetic composite failure");
        }));
        Assert.Equal(before, await Versions()); Assert.Equal(51, await runtime.ReadAsync(s => s.NextAudioFence));
        Assert.Equal("Before", await runtime.ReadAsync(s => s.VoiceTurns[id].Text)); Assert.Empty(await runtime.ReadAsync(s => s.VoiceTickets));
        Assert.Equal(session.Title, (await host.Services.GetRequiredService<Jarvis.Orchestrator.Sessions.ISessionReader>().GetAsync(session.Id))!.Title);
        await runtime.UpdateAsync(s => { s.VoiceTurns[id] = s.VoiceTurns[id] with { Text = "After" }; return true; });
        Assert.NotEqual(before, await Versions()); Assert.Equal(before.Split(':')[0], (await Versions()).Split(':')[0]);
        await using var verify = await factory.CreateDbContextAsync(); Assert.Equal("After", (await verify.RuntimeVoiceTurns.SingleAsync()).Text);
        await runtime.UpdateAsync(s => { s.NextTrainingSequence++; return true; });
        Assert.Equal("Cold answer", await runtime.ReadAsync(s => s.Learning[id].Answer)); Assert.Equal(124, await runtime.ReadAsync(s => s.NextTrainingSequence));
    }
}
