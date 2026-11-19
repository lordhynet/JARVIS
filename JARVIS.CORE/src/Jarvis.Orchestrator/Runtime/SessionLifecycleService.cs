using Jarvis.Contracts;
using Jarvis.Orchestrator.Sessions;

namespace Jarvis.Orchestrator.Runtime;

public sealed class SessionLifecycleService(ISessionStore store, DeviceService devices,
    ISessionNotifier notifier, TimeProvider clock, ICoreTransaction transaction)
{
    public Task<ConversationSession> TransitionAsync(Guid id, SessionStatus status, CancellationToken ct) =>
        Change(id, s =>
        {
            Guard.Require(Enum.IsDefined(status), 400, "Unknown status.");
            Guard.Require(s.Status != SessionStatus.Ended || status == SessionStatus.Ended, 409, "An ended session cannot be resumed.");
            Guard.Require(!Pending(s.Handoff), 409, "Finish or cancel the handoff first.");
            return s with { Status = status, ActiveDeviceId = status == SessionStatus.Active ? s.ActiveDeviceId : null };
        }, "session.status", ct);

    public Task<ConversationSession> ClaimAsync(Guid id, string deviceId, CancellationToken ct) =>
        Change(id, s =>
        {
            Guard.Require(s.Status == SessionStatus.Active, 409, "Session must be active.");
            Guard.Require(!Pending(s.Handoff) && (s.ActiveDeviceId is null || s.ActiveDeviceId == deviceId),
                409, "Another device owns the session. Use handoff.");
            return s with { ActiveDeviceId = deviceId };
        }, "session.claimed", ct, state =>
        {
            DeviceCredentialContext.RequireCurrent(state, deviceId);
            devices.RequireOnline(state, deviceId, false);
            var lease = state.AudioLease;
            Guard.Require(lease is null || lease.ExpiresAt <= clock.GetUtcNow() ||
                lease.SessionId == id && lease.DeviceId == deviceId, 409, "Another session has an active audio lease.");
        });

    public async Task<ConversationSession> StartAsync(Guid id, StartHandoffRequest request, CancellationToken ct)
    {
        var summary = Guard.Text(request.Summary, "Summary", 4000);
        Guard.Require(request.RequestId is null || request.RequestId != Guid.Empty, 400, "Use a nonempty handoff operation ID.");
        var hash = PrivacyData.Hash(System.Text.Json.JsonSerializer.Serialize(new { request.FromDeviceId, request.ToDeviceId, summary }));
        Guard.Require(request.FromDeviceId != request.ToDeviceId, 400, "Choose a different target device.");
        return await Change(id, s =>
        {
            if (request.RequestId is { } operation && s.Handoff?.Id == operation) {
                Guard.Require(s.Handoff.RequestHash == hash, 409, "Handoff operation ID was reused."); return s;
            }
            Guard.Require(s.Status == SessionStatus.Active && s.ActiveDeviceId == request.FromDeviceId,
                409, "Only the active device can start a handoff.");
            Guard.Require(!Pending(s.Handoff), 409, "A handoff is already pending.");
            Guard.Require(s.Checkpoints.Count < 2000, 409, "Conversation checkpoint capacity reached.");
            var now = clock.GetUtcNow();
            return s with
            {
                Checkpoints = [.. s.Checkpoints, new(Guid.NewGuid(), id, s.Checkpoints.Count + 1, summary, s.CurrentTask, now)],
                Handoff = new(request.RequestId ?? Guid.NewGuid(), request.FromDeviceId, request.ToDeviceId,
                    HandoffState.Checkpointed, now, now.AddSeconds(30), request.RequestId is null ? null : hash)
            };
        }, "handoff.checkpointed", ct, state =>
        {
            DeviceCredentialContext.RequireCurrent(state, request.FromDeviceId);
            devices.RequireOnline(state, request.FromDeviceId, false);
            devices.RequireOnline(state, request.ToDeviceId, true);
        });
    }

    public async Task<ConversationSession> AdvanceAsync(Guid id, string action, HandoffActionRequest request, CancellationToken ct)
    {
        var result = await Change(id, s =>
        {
            var h = s.Handoff;
            Guard.Require(h is not null && h.Id == request.HandoffId, 409, "Handoff does not match.");
            if (h!.State == HandoffState.TimedOut) return s;
            if (action == "cancel")
            {
                Guard.Require(request.DeviceId == h.FromDeviceId || request.DeviceId == h.ToDeviceId, 403, "Device is not part of this handoff.");
                if (h.State == HandoffState.Failed) return s;
                Guard.Require(Pending(h), 409, "Handoff is already finished.");
                return s with { Handoff = h with { State = HandoffState.Failed } };
            }
            var (expected, next, device) = action switch
            {
                "ready" => (HandoffState.Checkpointed, HandoffState.TargetReady, h.ToDeviceId),
                "release" => (HandoffState.TargetReady, HandoffState.AudioTransferred, h.FromDeviceId),
                "complete" => (HandoffState.AudioTransferred, HandoffState.Completed, h.ToDeviceId),
                _ => throw new DomainException(400, "Unknown handoff action.")
            };
            Guard.Require(request.DeviceId == device, 403, "Wrong device for this handoff action.");
            if (h.State == next) return s; // Retrying an acknowledged step is harmless.
            if (h.State == HandoffState.Completed && action is "ready" or "release" || h.State == HandoffState.AudioTransferred && action == "ready") return s;
            Guard.Require(h.State == expected, 409, "Handoff steps must run in order.");
            return s with
            {
                Handoff = h with { State = next },
                ActiveDeviceId = action == "release" ? null : action == "complete" ? h.ToDeviceId : s.ActiveDeviceId
            };
        }, $"handoff.{action}", ct, state =>
        {
            DeviceCredentialContext.RequireCurrent(state, request.DeviceId);
            if (action is "ready" or "complete") devices.RequireOnline(state, request.DeviceId, true);
        });
        Guard.Require(result.Handoff?.State != HandoffState.TimedOut, 409, "Handoff timed out. Claim the session to recover.");
        return result;
    }

    public async Task ExpireAsync(CancellationToken ct)
    {
        foreach (var session in await store.ListAsync(ct))
            if (Pending(session.Handoff) && session.Handoff!.ExpiresAt <= clock.GetUtcNow())
                await Change(session.Id, s => s, "handoff.timeout", ct);
    }

    private async Task<ConversationSession> Change(Guid id, Func<ConversationSession, ConversationSession> change,
        string eventType, CancellationToken ct, Action<RuntimeState>? validate = null)
    {
        var result = await transaction.ExecuteAsync(async writes =>
        {
            var state = writes.Runtime;
            validate?.Invoke(state);
            var updated = await writes.UpdateSessionAsync(id, s =>
            {
                if (Pending(s.Handoff) && s.Handoff!.ExpiresAt <= clock.GetUtcNow())
                    s = s with { Handoff = s.Handoff with { State = HandoffState.TimedOut } };
                return change(s) with { UpdatedAt = clock.GetUtcNow() };
            });
            Guard.Require(updated is not null, 404, "Session not found.");
            if (state.AudioLease?.SessionId == id &&
                (updated.Status != SessionStatus.Active || updated.ActiveDeviceId != state.AudioLease.DeviceId))
            {
                if (eventType == "handoff.release") state.AudioLease = null;
                else AudioFencing.Revoke(state);
            }
            return updated;
        }, ct);
        await notifier.PublishAsync(new(eventType, id, result.Status, result.ActiveDeviceId, clock.GetUtcNow()), ct);
        return result;
    }

    public static bool Pending(SessionHandoff? h) => h is not null && h.State is not
        (HandoffState.Completed or HandoffState.Failed or HandoffState.TimedOut);
}
