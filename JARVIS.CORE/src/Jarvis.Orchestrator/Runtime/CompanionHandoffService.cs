using Jarvis.Contracts;
using Jarvis.Orchestrator.Sessions;

namespace Jarvis.Orchestrator.Runtime;

public sealed class CompanionHandoffService(IRuntimeReader runtime, ISessionReader sessions, SessionLifecycleService lifecycle, TimeProvider clock)
{
    private static void Actor(RuntimeState state, string device) => DeviceCredentialContext.RequireCurrent(state, device);
    public async Task<CompanionHandoffView> ControlAsync(CompanionHandoffRequest request, CancellationToken ct = default)
    {
        Guard.Text(request.DeviceId, "Device ID", 80);
        Guard.Require(request.Action is "poll" or "start" or "ready" or "release" or "complete" or "cancel", 400, "Unknown handoff control.");
        await runtime.ReadAsync(s => { Actor(s, request.DeviceId); Guard.Require(s.Devices.ContainsKey(request.DeviceId), 404, "Device not registered."); return true; }, ct);
        if (request.Action != "poll") {
            Guard.Require(request.SessionId is { } id && id != Guid.Empty, 400, "Choose an existing conversation.");
            if (request.Action is "start" or "ready" or "complete") Guard.Require(request.Locked == false, 403, "Unlock this device before sending or accepting a conversation.");
            if (request.Action == "start") {
                Guard.Require(request.RequestId is { } operation && operation != Guid.Empty && request.TargetDeviceId is { Length: > 0 and <= 80 }, 400, "Choose a target and a stable operation ID.");
                await runtime.ReadAsync(s => {
                    Actor(s, request.DeviceId);
                    Guard.Require(s.AudioLease is not { } lease || lease.ExpiresAt <= clock.GetUtcNow() || lease.DeviceId != request.DeviceId || lease.SessionId == request.SessionId,
                        409, "The live conversation changed. Refresh before moving it."); return true;
                }, ct);
                await lifecycle.StartAsync(request.SessionId!.Value, new(request.DeviceId, request.TargetDeviceId!, "Continue the same conversation on the selected device.", request.RequestId), ct);
            } else {
                Guard.Require(request.HandoffId is { } handoff && handoff != Guid.Empty, 400, "Choose a handoff ID.");
                var current = await sessions.ReadHandoffAsync(request.SessionId!.Value, ct);
                Guard.Require(current is not null && current.Id == request.HandoffId && (current.FromDeviceId == request.DeviceId || current.ToDeviceId == request.DeviceId),
                    403, "This device is not a participant in that handoff.");
                if (request.Action == "release") Guard.Require(request.AudioStopped, 409, "Stop and drain source capture and playback before acknowledging release.");
                await lifecycle.AdvanceAsync(request.SessionId!.Value, request.Action, new(request.HandoffId!.Value, request.DeviceId), ct);
            }
        }
        return await PollAsync(request.DeviceId, request.Locked, ct);
    }
    private async Task<CompanionHandoffView> PollAsync(string device, bool? locked, CancellationToken ct)
    {
        var candidates = await sessions.ListPreparationAsync(device, Guid.Empty, ct);
        return await runtime.ReadAsync(s => {
            Actor(s, device); var now = clock.GetUtcNow();
            var offers = candidates.Where(c => c.Status == SessionStatus.Active && SessionLifecycleService.Pending(c.Handoff) && c.Handoff!.ExpiresAt > now &&
                    (c.Handoff.FromDeviceId == device || c.Handoff.ToDeviceId == device)).OrderByDescending(c => c.UpdatedAt).Take(10)
                .Select(c => new CompanionHandoffItem(c.Id, c.Handoff!.Id, c.Handoff.FromDeviceId, c.Handoff.ToDeviceId, c.Handoff.State, c.Handoff.ExpiresAt, c.Handoff.ToDeviceId == device)).ToArray();
            var targets = locked == false ? s.Devices.Values.Where(d => d.Registration.DeviceId != device && d.CapabilitiesVerified && now - d.LastSeenAt <= DeviceService.OnlineWindow &&
                d.Registration.Capabilities.CanListen && d.Registration.Capabilities.CanSpeak && !(d.Recording?.Until > now) &&
                (!s.DeviceCredentials.TryGetValue(d.Registration.DeviceId, out var key) || !key.Revoked) &&
                !(s.AudioLease is { Locked: true } lease && lease.DeviceId == d.Registration.DeviceId))
                .OrderBy(d => d.Registration.DeviceId).Take(50).Select(d => new CompanionHandoffTarget(d.Registration.DeviceId, d.Registration.Name, d.Registration.Kind,
                    d.Registration.Capabilities.Extra.Contains(VoiceAccessRules.BrowserCompanionTag))).ToArray() : [];
            var owned = candidates.Where(c => c.Status == SessionStatus.Active && c.ActiveDeviceId == device).ToArray();
            var selected = s.AudioLease is { } current && current.DeviceId == device && current.ExpiresAt > now
                ? owned.Where(c => c.Id == current.SessionId).Select(c => (Guid?)c.Id).FirstOrDefault()
                : owned.OrderByDescending(c => c.UpdatedAt).Select(c => (Guid?)c.Id).FirstOrDefault();
            return new CompanionHandoffView(locked == false ? selected : null, offers, targets);
        }, ct);
    }
}
