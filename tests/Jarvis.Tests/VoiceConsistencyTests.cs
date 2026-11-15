using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

internal sealed class FailVoiceProjection(IRuntimeStore inner) : IRuntimeStore
{
    public Task<T> ReadAsync<T>(Func<RuntimeState, T> read, CancellationToken ct = default) => inner.ReadAsync(read, ct);
    public Task<T> UpdateAsync<T>(Func<RuntimeState, T> update, CancellationToken ct = default) => throw new IOException("Injected crash before projection commit.");
}
public sealed class VoiceConsistencyTests
{
    [Fact]
    public async Task Idle_repair_checks_pending_markers_without_listing_or_loading_transcripts()
    {
        var inner = new InMemorySessionStore();
        var sessions = new NoTranscriptReadSessionStore(inner);
        var runtime = new InMemoryRuntimeStore();
        var repair = new MemoryVoiceRepairStore(sessions, runtime, new MemoryPrivacyBoundary());
        Assert.Equal(0, await repair.RepairTranscriptsAsync(default));
        Assert.Equal(0, sessions.TranscriptReads);
        Assert.Empty(await runtime.ReadAsync(s => s.VoiceTurns));
    }

    [Fact]
    public async Task Interrupted_projection_repairs_from_canonical_transcript_without_duplicate_or_chat_backfill()
    {
        using var host = new JarvisApiFactory(); var sessions = host.Services.GetRequiredService<ISessionStore>();
        var runtime = host.Services.GetRequiredService<IRuntimeStore>(); var now = DateTimeOffset.UtcNow; var id = Guid.NewGuid(); var turn = Guid.NewGuid();
        await sessions.AddAsync(new(id, ActivityType.General, "Voice", SessionStatus.Active, "phone", null, now, now, [], Messages: [new(Guid.NewGuid(), "user", "Ordinary text", now)]));
        var failing = new VoiceTranscriptService(sessions, new FailVoiceProjection(runtime), TimeProvider.System);
        await Assert.ThrowsAsync<IOException>(() => failing.RecordAsync(id, turn, "user", "Captured words", 2.75, default));
        Assert.Empty(await runtime.ReadAsync(s => s.VoiceTurns));
        var marker = (await sessions.GetAsync(id))!.Messages!.Single(m => m.RequestId == turn); Assert.True(marker.IsVoice); Assert.Equal(2.75, marker.SpeechSeconds);
        var repair = host.Services.GetRequiredService<IVoiceRepairStore>(); Assert.Equal(1, await repair.RepairTranscriptsAsync(default)); Assert.Equal(0, await repair.RepairTranscriptsAsync(default));
        var recorded = Assert.Single(await runtime.ReadAsync(s => s.VoiceTurns.Values.ToArray())); Assert.Equal(turn, recorded.Id); Assert.Equal(marker.At, recorded.At);
        Assert.Empty((await sessions.GetAsync(id))!.PendingVoiceProjectionIds!);
        var service = host.Services.GetRequiredService<VoiceTranscriptService>();
        await service.RecordAsync(id, turn, "user", "Captured words", 2.75, default);
        await Assert.ThrowsAsync<DomainException>(() => service.RecordAsync(id, turn, "user", "Different text", 2.75, default));
        Assert.Single(await runtime.ReadAsync(s => s.VoiceTurns.Values.ToArray()));
        Assert.Empty(await runtime.ReadAsync(s => s.UsageReservations));
    }
    [Fact]
    public async Task Interrupted_session_transition_revokes_orphan_lease_with_drain_and_preserves_current_owner()
    {
        var runtime = new InMemoryRuntimeStore(); var sessions = new InMemorySessionStore(); var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid(); await sessions.AddAsync(new(id, ActivityType.General, "Voice", SessionStatus.Active, "phone", null, now, now, []));
        var lease = new AudioLease(id, "phone", 7, now.AddSeconds(15)); await runtime.UpdateAsync(s => { s.AudioLease = lease; return true; });
        var owner = new AudioOwnership(runtime, sessions, new DeviceService(runtime, TimeProvider.System), TimeProvider.System);
        Assert.False(await owner.RepairAsync(default));
        await sessions.UpdateAsync(id, s => s with { Status = SessionStatus.Paused, ActiveDeviceId = null });
        Assert.False(await owner.CurrentAsync(lease, default)); Assert.True(await owner.RepairAsync(default));
        Assert.False(await owner.RepairAsync(default));
        var state = await runtime.ReadAsync(s => s); Assert.Null(state.AudioLease); Assert.Equal(lease, state.AudioDrainLease); Assert.Equal(lease.ExpiresAt, state.AudioDrainUntil);
        Assert.Throws<DomainException>(() => AudioFencing.RequireAvailable(state, now));
    }
}

internal sealed class NoTranscriptReadSessionStore(InMemorySessionStore inner) : ISessionStore
{
    public int TranscriptReads { get; private set; }
    public Task<ConversationSession?> GetAsync(Guid id, CancellationToken ct = default) { TranscriptReads++; return inner.GetAsync(id, ct); }
    public Task<IReadOnlyList<ConversationSession>> ListAsync(CancellationToken ct = default) { TranscriptReads++; throw new InvalidOperationException("Idle repair must not list transcript sessions."); }
    public Task<IReadOnlyList<PendingVoiceProjection>> ListPendingVoiceProjectionsAsync(int limit, CancellationToken ct = default) => inner.ListPendingVoiceProjectionsAsync(limit, ct);
    public Task<ConversationSession> AddAsync(ConversationSession session, CancellationToken ct = default) => inner.AddAsync(session, ct);
    public Task<ConversationSession?> UpdateAsync(Guid id, Func<ConversationSession, ConversationSession> update, CancellationToken ct = default) => inner.UpdateAsync(id, update, ct);
    public Task<ConversationSession?> AppendCheckpointAsync(Guid sessionId, Func<ConversationSession, SessionCheckpoint> createCheckpoint, DateTimeOffset now, CancellationToken ct = default) => inner.AppendCheckpointAsync(sessionId, createCheckpoint, now, ct);
}
