using System.Text.Json;
using Jarvis.Api.Voice;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Coaching;
using Jarvis.Orchestrator.Domains;
using Jarvis.Orchestrator.Intelligence;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Jarvis.Orchestrator.Training;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed class RestrictedVoiceHistoryTests
{
    [Fact]
    public async Task Restricted_history_is_auditable_but_never_recent_summary_or_learning_input()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException("Restricted speech must not dispatch a provider."));
        var ticket = await LockedVoiceBoundaryTests.StartAsync(host.Services, locked: false);
        var sessionId = ticket.Lease.SessionId; var transcripts = host.Services.GetRequiredService<VoiceTranscriptService>();
        var restrictedId = Guid.NewGuid();
        await transcripts.RecordAsync(sessionId, restrictedId, ChatRoles.User, "LOCKED_HISTORY_MARKER", 2, default, restricted: true);
        var session = (await host.Services.GetRequiredService<ISessionStore>().GetAsync(sessionId))!;
        Assert.True(session.Messages!.Single(m => m.RequestId == restrictedId).Restricted);
        Assert.DoesNotContain("LOCKED_HISTORY_MARKER", JsonSerializer.Serialize(VoiceConversationMemory.Recent(session)));
        await Assert.ThrowsAsync<DomainException>(() => host.Services.GetRequiredService<VoicePracticeService>().StartAsync(sessionId, restrictedId, TrainingMode.RapidDrill, default));
        await Assert.ThrowsAsync<DomainException>(() => host.Services.GetRequiredService<VoiceRecallService>().RunAsync(sessionId, "start", restrictedId, "None", default));
        await Assert.ThrowsAsync<DomainException>(() => host.Services.GetRequiredService<VoiceCoachingService>().RunAsync(sessionId, "start", restrictedId, "point-first", null, null, null, default));
        await Assert.ThrowsAsync<DomainException>(() => host.Services.GetRequiredService<IDomainFactory>().ProposeCapturedAsync(sessionId, restrictedId, default));
        for (var i = 0; i < 25; i++) await transcripts.RecordAsync(sessionId, Guid.NewGuid(), ChatRoles.User, "Trusted speech " + i, 1, default);
        session = (await host.Services.GetRequiredService<ISessionStore>().GetAsync(sessionId))!;
        Assert.NotNull(session.VoiceSummary); Assert.DoesNotContain("LOCKED_HISTORY_MARKER", JsonSerializer.Serialize(session.VoiceSummary));
        Assert.Contains(session.Messages!, m => m.RequestId == restrictedId && m.Restricted);
        var changed = await Assert.ThrowsAsync<DomainException>(() => transcripts.RecordAsync(sessionId, restrictedId, ChatRoles.User, "LOCKED_HISTORY_MARKER", 2, default));
        Assert.Equal(409, changed.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Personal_tool_gate_denies_missing_and_mismatched_audio_lease(bool mismatched)
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException("No provider request."));
        var ticket = await LockedVoiceBoundaryTests.StartAsync(host.Services, locked: false);
        var id = mismatched ? Guid.NewGuid() : ticket.Lease.SessionId;
        if (!mismatched) await host.Services.GetRequiredService<IRuntimeStore>().UpdateAsync(s => { s.AudioLease = null; return true; });
        var result = await host.Services.GetRequiredService<CoreVoiceTools>().ExecuteAsync("core_context", "{}", id, new HashSet<Guid>(), default);
        Assert.Contains("unavailable while the device is locked", result); Assert.DoesNotContain("Private", result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(null)]
    public async Task Locked_or_unknown_Windows_wake_cannot_advance_an_incoming_handoff(bool? locked)
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException("No provider request."));
        var services = host.Services; var devices = services.GetRequiredService<DeviceService>();
        await devices.RegisterAsync(new("handoff-phone", "Phone", DeviceKind.AndroidCompanion, new(true, true, false, false, []))); await devices.VerifyAsync("handoff-phone");
        await devices.RegisterAsync(new("handoff-laptop", "Laptop", DeviceKind.WindowsAgent, new(true, true, false, false, ["audio-only"]))); await devices.VerifyAsync("handoff-laptop");
        var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var sessions = services.GetRequiredService<ISessionStore>();
        await sessions.AddAsync(new(id, ActivityType.General, "Private phone conversation", SessionStatus.Active, "handoff-phone", null, now, now, []));
        var audio = services.GetRequiredService<AudioOwnership>(); var lease = await audio.StartAsync(new(id, "handoff-phone", true), default);
        var handoff = await services.GetRequiredService<SessionLifecycleService>().StartAsync(id, new("handoff-phone", "handoff-laptop", "Private checkpoint"), default);
        var error = await Assert.ThrowsAsync<DomainException>(() => services.GetRequiredService<CompanionVoiceService>().PrepareAsync(new(Guid.NewGuid(), "handoff-laptop", true, locked), default));
        Assert.Equal(409, error.StatusCode); Assert.Contains("Unlock", error.Message);
        var stored = (await sessions.GetAsync(id))!;
        Assert.Equal(handoff.Handoff, stored.Handoff); Assert.Equal("handoff-phone", stored.ActiveDeviceId); Assert.True(await audio.CurrentAsync(lease.Lease, default));
    }

}

public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task Restricted_marker_survives_PostgreSQL_and_runtime_projection_restart()
    {
        await using var database = await TestDatabase.CreateAsync(); var clock = new VoiceFixtureClock();
        var id = Guid.NewGuid(); var turn = Guid.NewGuid();
        using (var first = new PostgresApiFactory(database.ConnectionString, clock)) {
            var now = clock.GetUtcNow(); await first.Services.GetRequiredService<ISessionStore>().AddAsync(new(id, ActivityType.General, "Locked audit", SessionStatus.Active, null, null, now, now, []));
            await first.Services.GetRequiredService<VoiceTranscriptService>().RecordAsync(id, turn, ChatRoles.User, "Restricted audit", 1, default, restricted: true);
        }
        using var restarted = new PostgresApiFactory(database.ConnectionString, clock);
        var session = (await restarted.Services.GetRequiredService<ISessionStore>().GetAsync(id))!;
        Assert.True(Assert.Single(session.Messages!).Restricted);
        Assert.True((await restarted.Services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.VoiceTurns[turn])).Restricted);
        Assert.Empty(VoiceConversationMemory.Recent(session));
    }
}
