using System.Security.Cryptography;
using System.Text;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Sessions;

namespace Jarvis.Orchestrator.Runtime;

public sealed class AudioOwnership(IRuntimeStore store, ISessionReader sessions, DeviceService devices, TimeProvider clock,
    ICoreTransaction transaction)
{
    public Task<AudioLease> StartCalibrationAsync(string deviceId, CancellationToken ct) => store.UpdateAsync(s => {
        Guard.Require(s.Devices.TryGetValue(deviceId, out var device) && device.Registration.Capabilities.CanListen && device.Registration.Capabilities.CanSpeak,
            409, "Register an audio-capable device before testing it.");
        RequireCredentialEnabled(s, deviceId); var now = clock.GetUtcNow();
        AudioFencing.RequireAvailable(s, now);
        var lease = new AudioLease(Guid.Empty, deviceId, s.NextAudioFence++, now.AddSeconds(15)); s.AudioLease = lease; return lease;
    }, ct);
    public Task<AudioLease> StartLocalAsync(Guid sessionId, string deviceId, CancellationToken ct) =>
        transaction.ExecuteAsync(async writes =>
        {
            await RequireClaimAsync(writes, sessionId, deviceId);
            return StartLease(writes.Runtime, sessionId, deviceId, clock.GetUtcNow());
        }, ct);

    public Task<VoiceTicket> StartAsync(VoiceStartRequest request, CancellationToken ct, int windowSeconds = VoiceWindowLimits.DefaultSeconds)
    {
        Guard.Require(request.AllowCloudProcessing, 400, "Live voice requires explicit cloud-processing consent.");
        var window = new VoiceWindowLimits(windowSeconds);
        return transaction.ExecuteAsync(async writes =>
        {
            await RequireClaimAsync(writes, request.SessionId, request.DeviceId);
            var now = clock.GetUtcNow();
            var s = writes.Runtime;
            var lease = StartLease(s, request.SessionId, request.DeviceId, now) with {
                Locked = VoiceAccessRules.ReportedState(s, request.DeviceId, request.Locked), ReportedLocked = request.Locked };
            s.AudioLease = lease;
            var ticket = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var hash = Hash(ticket);
            foreach (var key in s.VoiceTickets.Where(x => x.Value.ExpiresAt <= now).Select(x => x.Key).ToList()) s.VoiceTickets.Remove(key);
            s.VoiceTickets.Add(hash, new(hash, lease, now.AddSeconds(15), window.Seconds));
            return new VoiceTicket(ticket, lease, window.Seconds, VoiceWindowLimits.SampleRate);
        }, ct);
    }

    private async Task RequireClaimAsync(ICoreTransactionWrites writes, Guid sessionId, string deviceId)
    {
        devices.RequireOnline(writes.Runtime, deviceId, true);
        var session = await writes.ReadAudioStateAsync(sessionId);
        Guard.Require(session is { Status: SessionStatus.Active } && session.ActiveDeviceId == deviceId,
            409, "Claim an active session on this verified device first.");
    }

    private AudioLease StartLease(RuntimeState state, Guid sessionId, string deviceId, DateTimeOffset now)
    {
        RequireCredentialEnabled(state, deviceId);
        AudioFencing.RequireAvailable(state, now);
        var lease = new AudioLease(sessionId, deviceId, state.NextAudioFence, now.AddSeconds(15));
        state.NextAudioFence = checked(state.NextAudioFence + 1);
        state.AudioLease = lease;
        return lease;
    }
    public async Task<AudioLease> ConsumeAsync(string ticket, CancellationToken ct) =>
        (await ConsumeTicketAsync(ticket, ct)).Lease;

    public Task<VoiceTicketRecord> ConsumeTicketAsync(string ticket, CancellationToken ct)
    {
        Guard.Require(ticket.Length <= 100, 401, "Invalid voice ticket."); var hash = Hash(ticket);
        return store.UpdateAsync(s =>
        {
            Guard.Require(s.VoiceTickets.Remove(hash, out var entry) && entry.ExpiresAt > clock.GetUtcNow(), 401, "Voice ticket expired or was already consumed.");
            Require(s, entry.Lease); return entry;
        }, ct);
    }
    public Task<AudioLease> RenewAsync(AudioLease lease, CancellationToken ct, bool? locked = null) =>
        transaction.ExecuteAsync(async writes =>
        {
            if (lease.SessionId != Guid.Empty)
            {
                var session = await writes.ReadAudioStateAsync(lease.SessionId);
                Guard.Require(session is { Status: SessionStatus.Active } && session.ActiveDeviceId == lease.DeviceId,
                    409, "Session audio ownership changed.");
            }
            var s = writes.Runtime;
            Require(s, lease);
            var current = s.AudioLease!;
            var updated = current with { ExpiresAt = clock.GetUtcNow().AddSeconds(15),
                Locked = current.Locked || VoiceAccessRules.ReportedState(s, lease.DeviceId, locked), ReportedLocked = locked };
            s.AudioLease = updated; return updated;
        }, ct);

    // Only Core activates a window. An unlock report cannot relax the active window.
    public Task<AudioLease> ActivateVoiceWindowAsync(AudioLease lease, CancellationToken ct) => store.UpdateAsync(s => {
        Require(s, lease);
        var current = s.AudioLease!;
        var active = current with { Locked = VoiceAccessRules.ReportedState(s, current.DeviceId, current.ReportedLocked) };
        s.AudioLease = active; return active;
    }, ct);
    public async Task<bool> CurrentAsync(AudioLease lease, CancellationToken ct) =>
        await ReadCurrentAsync(lease, ct) is not null;

    internal async Task<AudioLease?> ReadCurrentAsync(AudioLease lease, CancellationToken ct)
    {
        var session = await sessions.ReadAudioStateAsync(lease.SessionId, ct);
        if (session is not { Status: SessionStatus.Active } || session.ActiveDeviceId != lease.DeviceId) return null;
        // Revocation/rotation atomically clears this fence. No full runtime or transcript read.
        var current = await store.ReadAudioLeaseAsync(ct);
        return current is not null && AudioFenceChanges.Matches(lease, current) && current.ExpiresAt > clock.GetUtcNow()
            ? current : null;
    }
    public Task<bool> ReleaseAsync(AudioLease lease, CancellationToken ct) => store.UpdateAsync(s =>
    {
        if (s.AudioLease?.Fence == lease.Fence && s.AudioLease.SessionId == lease.SessionId && s.AudioLease.DeviceId == lease.DeviceId) { s.AudioLease = null; return true; }
        if (s.AudioDrainLease?.Fence == lease.Fence && s.AudioDrainLease.SessionId == lease.SessionId && s.AudioDrainLease.DeviceId == lease.DeviceId) {
            s.AudioDrainLease = null; s.AudioDrainUntil = null; return true;
        }
        return false;
    }, ct);
    private void Require(RuntimeState s, AudioLease lease)
    {
        RequireCredentialEnabled(s, lease.DeviceId);
        Guard.Require(s.AudioLease?.Fence == lease.Fence && s.AudioLease.SessionId == lease.SessionId &&
            s.AudioLease.DeviceId == lease.DeviceId && s.AudioLease.ExpiresAt > clock.GetUtcNow(), 409, "Live audio lease expired or was replaced.");
    }
    private void RequireCredentialEnabled(RuntimeState s, string deviceId)
    {
        DeviceCredentialContext.RequireCurrent(s, deviceId);
        Guard.Require(!(s.Devices.GetValueOrDefault(deviceId)?.Recording?.Until > clock.GetUtcNow()), 409, "This device is recording an interview. Stop recording before using JARVIS voice.");
        Guard.Require(!s.DeviceCredentials.TryGetValue(deviceId, out var credential) || !credential.Revoked, 401, "The device credential was revoked.");
    }
    private static string Hash(string ticket) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ticket)));
}
