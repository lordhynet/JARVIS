using System.Security.Cryptography;
using System.Text;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Sessions;

namespace Jarvis.Orchestrator.Runtime;

/// <summary>Core owns conversation selection, creation, resumption and device claims.</summary>
public sealed class CompanionVoiceService(ISessionStore sessions, DeviceService devices,
    SessionLifecycleService lifecycle, IRuntimeReader runtime, TimeProvider clock)
{
    private readonly SemaphoreSlim preparation = new(1, 1);
    public async Task<CompanionVoiceSession> PrepareAsync(CompanionVoiceRequest request, CancellationToken ct)
    {
        Guard.Require(request.RequestId != Guid.Empty && request.AllowCloudProcessing, 400, "An explicit, consented voice start is required.");
        var deviceId = Guard.Text(request.DeviceId, "DeviceId", 80);
        await devices.RequireOnlineAsync(deviceId, true, ct);
        await preparation.WaitAsync(ct);
        try
        {
            var incoming = (await sessions.ListAsync(ct)).Where(s => s.Status == SessionStatus.Active &&
                SessionLifecycleService.Pending(s.Handoff) && s.Handoff!.ToDeviceId == deviceId && s.Handoff.ExpiresAt > clock.GetUtcNow()).ToList();
            Guard.Require(incoming.Count <= 1, 409, "More than one incoming conversation is waiting. Select one on the source device.");
            ConversationSession? transferred = null;
            if (incoming.Count == 1)
            {
                var target = incoming[0]; var handoff = target.Handoff!;
                if (handoff.State == HandoffState.Checkpointed)
                    target = await lifecycle.AdvanceAsync(target.Id, "ready", new(handoff.Id, deviceId), ct);
                // Core owns the handshake. The phone only requests voice start; it never chooses or steals a source session.
                using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct);
                waiting.CancelAfter(TimeSpan.FromSeconds(30));
                try {
                    while (target.Handoff?.Id == handoff.Id && target.Handoff.State == HandoffState.TargetReady &&
                        target.Handoff.ExpiresAt > clock.GetUtcNow())
                    {
                        await Task.Delay(250, waiting.Token);
                        target = await sessions.GetAsync(target.Id, waiting.Token) ?? throw new DomainException(409, "Incoming conversation was removed.");
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
                    throw new DomainException(409, "Source audio has not stopped. Retry after the source releases the conversation.");
                }
                Guard.Require(target.Handoff?.Id == handoff.Id && target.Handoff.State == HandoffState.AudioTransferred,
                    409, "Incoming handoff was cancelled or expired. Start again from the source device.");
                transferred = await lifecycle.AdvanceAsync(target.Id, "complete", new(handoff.Id, deviceId), ct);
            }
            Guard.Require(await runtime.ReadAsync(s => (s.AudioLease is null || s.AudioLease.ExpiresAt <= clock.GetUtcNow()) &&
                (s.AudioDrainUntil is null || s.AudioDrainUntil <= clock.GetUtcNow()), ct),
                409, "Another voice connection is active. Stop it before starting this one.");
            var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"companion-voice-v1:{deviceId}:{request.RequestId}"))[..16]);
            var all = await sessions.ListAsync(ct);
            var session = transferred ?? all.FirstOrDefault(s => s.Id == id || s.Checkpoints.Any(c => c.Id == id));
            if (session is null)
                session = all.Where(s => s.Status != SessionStatus.Ended && s.Checkpoints.Count < 1800 && (s.Messages?.Count ?? 0) < 1800 &&
                    (s.ActiveDeviceId == deviceId || s.Status == SessionStatus.Paused && s.ActiveDeviceId is null && s.Title == "JARVIS voice"))
                    .OrderByDescending(s => s.UpdatedAt).FirstOrDefault();
            if (session is null)
            {
                var now = clock.GetUtcNow();
                try { session = await sessions.AddAsync(new(id, ActivityType.General, "JARVIS voice", SessionStatus.Active, null, null, now, now, []), ct); }
                catch (Exception) when (!ct.IsCancellationRequested) { session = await sessions.GetAsync(id, ct); if (session is null) throw; }
            }
            Guard.Require(session.Status != SessionStatus.Ended, 409, "That conversation has ended. Start a new voice request.");
            if (session.Status == SessionStatus.Paused) session = await lifecycle.TransitionAsync(session.Id, SessionStatus.Active, ct);
            var claimed = await lifecycle.ClaimAsync(session.Id, deviceId, ct);
            await sessions.UpdateAsync(claimed.Id, current => {
                if (current.Checkpoints.Any(c => c.Id == id)) return current;
                Guard.Require(current.Checkpoints.Count < 2000, 409, "Conversation connection history is full. Start a new voice request.");
                return current with { Checkpoints = [..current.Checkpoints, new(id, current.Id, current.Checkpoints.Count + 1,
                    "Audio companion connection prepared", current.CurrentTask, clock.GetUtcNow())] };
            }, ct);
            return new(claimed.Id);
        }
        finally { preparation.Release(); }
    }
}
