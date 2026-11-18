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
        var device = await devices.RequireOnlineAsync(deviceId, true, ct);
        var id = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"companion-voice-v1:{deviceId}:{request.RequestId}"))[..16]);
        await preparation.WaitAsync(ct);
        try
        {
            var candidates = await sessions.ListPreparationAsync(deviceId, id, ct);
            var incoming = candidates.Where(s => s.Status == SessionStatus.Active &&
                SessionLifecycleService.Pending(s.Handoff) && s.Handoff!.ToDeviceId == deviceId && s.Handoff.ExpiresAt > clock.GetUtcNow()).ToList();
            Guard.Require(incoming.Count <= 1, 409, "More than one incoming conversation is waiting. Select one on the source device.");
            ConversationSession? transferred = null;
            if (incoming.Count == 1)
            {
                Guard.Require(!VoiceAccessRules.RequiresLockReport(device) || request.Locked == false,
                    409, "Unlock the target Windows device before completing an incoming conversation handoff.");
                var targetId = incoming[0].Id; var handoff = incoming[0].Handoff!;
                if (handoff.State == HandoffState.Checkpointed)
                    handoff = (await lifecycle.AdvanceAsync(targetId, "ready", new(handoff.Id, deviceId), ct)).Handoff!;
                var pending = handoff;
                // Core owns the handshake. The phone only requests voice start; it never chooses or steals a source session.
                using var waiting = CancellationTokenSource.CreateLinkedTokenSource(ct);
                waiting.CancelAfter(TimeSpan.FromSeconds(30));
                try {
                    while (pending?.Id == handoff.Id && pending.State == HandoffState.TargetReady &&
                        pending.ExpiresAt > clock.GetUtcNow())
                    {
                        await Task.Delay(250, waiting.Token);
                        pending = await sessions.ReadHandoffAsync(targetId, waiting.Token);
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
                    throw new DomainException(409, "Source audio has not stopped. Retry after the source releases the conversation.");
                }
                Guard.Require(pending?.Id == handoff.Id && pending.State == HandoffState.AudioTransferred,
                    409, "Incoming handoff was cancelled or expired. Start again from the source device.");
                transferred = await lifecycle.AdvanceAsync(targetId, "complete", new(handoff.Id, deviceId), ct);
            }
            var audio = await runtime.ReadAudioLeaseStateAsync(ct);
            Guard.Require(audio.IsAvailable(clock.GetUtcNow()),
                409, "Another voice connection is active. Stop it before starting this one.");
            var all = candidates;
            var selected = all.FirstOrDefault(s => s.Id == id || s.CheckpointIds.Contains(id));
            if (selected is null)
                selected = all.Where(s => s.Status != SessionStatus.Ended && s.CheckpointIds.Count < 1800 && s.MessageCount < 1800 &&
                    (s.ActiveDeviceId == deviceId || s.Status == SessionStatus.Paused && s.ActiveDeviceId is null && s.Title == "JARVIS voice"))
                    .OrderByDescending(s => s.UpdatedAt).FirstOrDefault();
            var session = transferred ?? (selected is null ? null : await sessions.GetAsync(selected.Id, ct));
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
