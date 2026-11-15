using System.Security.Cryptography;
using System.Text;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Sessions;

namespace Jarvis.Orchestrator.Runtime;

public sealed class AudioOwnership(IRuntimeStore store, ISessionReader sessions, DeviceService devices, TimeProvider clock)
{
    public Task<AudioLease> StartCalibrationAsync(string deviceId, CancellationToken ct) => store.UpdateAsync(s => {
        Guard.Require(s.Devices.TryGetValue(deviceId, out var device) && device.Registration.Capabilities.CanListen && device.Registration.Capabilities.CanSpeak,
            409, "Register an audio-capable device before testing it.");
        RequireCredentialEnabled(s, deviceId); var now = clock.GetUtcNow();
        AudioFencing.RequireAvailable(s, now);
        var lease = new AudioLease(Guid.Empty, deviceId, s.NextAudioFence++, now.AddSeconds(15)); s.AudioLease = lease; return lease;
    }, ct);
    public async Task<AudioLease> StartLocalAsync(Guid sessionId, string deviceId, CancellationToken ct)
    {
        await devices.RequireOnlineAsync(deviceId, true, ct);
        var session = await sessions.ReadAudioStateAsync(sessionId, ct);
        Guard.Require(session is { Status: SessionStatus.Active } && session.ActiveDeviceId == deviceId, 409, "Claim an active session on this verified device first.");
        return await store.UpdateAsync(s => {
            RequireCredentialEnabled(s, deviceId); var now = clock.GetUtcNow();
            AudioFencing.RequireAvailable(s, now);
            var lease = new AudioLease(sessionId, deviceId, s.NextAudioFence++, now.AddSeconds(15)); s.AudioLease = lease; return lease;
        }, ct);
    }
    public async Task<VoiceTicket> StartAsync(VoiceStartRequest request, CancellationToken ct)
    {
        Guard.Require(request.AllowCloudProcessing, 400, "Live voice requires explicit cloud-processing consent.");
        await devices.RequireOnlineAsync(request.DeviceId, true, ct);
        var session = await sessions.GetAsync(request.SessionId, ct);
        Guard.Require(session is { Status: SessionStatus.Active } && session.ActiveDeviceId == request.DeviceId, 409, "Claim an active session on this verified device first.");
        var ticket = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)); var hash = Hash(ticket);
        return await store.UpdateAsync(s =>
        {
            var now = clock.GetUtcNow();
            RequireCredentialEnabled(s, request.DeviceId);
            AudioFencing.RequireAvailable(s, now);
            var lease = new AudioLease(request.SessionId, request.DeviceId, s.NextAudioFence, now.AddSeconds(15));
            s.NextAudioFence = checked(s.NextAudioFence + 1); s.AudioLease = lease;
            foreach (var key in s.VoiceTickets.Where(x => x.Value.ExpiresAt <= now).Select(x => x.Key).ToList()) s.VoiceTickets.Remove(key);
            s.VoiceTickets.Add(hash, new(hash, lease, now.AddSeconds(15)));
            return new VoiceTicket(ticket, lease, 60, 24000);
        }, ct);
    }
    public Task<AudioLease> ConsumeAsync(string ticket, CancellationToken ct)
    {
        Guard.Require(ticket.Length <= 100, 401, "Invalid voice ticket."); var hash = Hash(ticket);
        return store.UpdateAsync(s =>
        {
            Guard.Require(s.VoiceTickets.Remove(hash, out var entry) && entry.ExpiresAt > clock.GetUtcNow(), 401, "Voice ticket expired or was already consumed.");
            Require(s, entry.Lease); return entry.Lease;
        }, ct);
    }
    public async Task<AudioLease> RenewAsync(AudioLease lease, CancellationToken ct)
    {
        if (lease.SessionId != Guid.Empty) {
            var session = await sessions.ReadAudioStateAsync(lease.SessionId, ct);
            Guard.Require(session is { Status: SessionStatus.Active } && session.ActiveDeviceId == lease.DeviceId, 409, "Session audio ownership changed.");
        }
        return await store.UpdateAsync(s =>
        {
            Require(s, lease); var updated = lease with { ExpiresAt = clock.GetUtcNow().AddSeconds(15) }; s.AudioLease = updated; return updated;
        }, ct);
    }
    public async Task<bool> CurrentAsync(AudioLease lease, CancellationToken ct)
    {
        var session = await sessions.ReadAudioStateAsync(lease.SessionId, ct);
        if (session is not { Status: SessionStatus.Active } || session.ActiveDeviceId != lease.DeviceId) return false;
        // Revocation/rotation atomically clears this fence. No full runtime or transcript read.
        var current = await store.ReadAudioLeaseAsync(ct);
        return current?.Fence == lease.Fence && current.SessionId == lease.SessionId && current.DeviceId == lease.DeviceId && current.ExpiresAt > clock.GetUtcNow();
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
    private static void RequireCredentialEnabled(RuntimeState s, string deviceId)
    {
        DeviceCredentialContext.RequireCurrent(s, deviceId);
        Guard.Require(!s.DeviceCredentials.TryGetValue(deviceId, out var credential) || !credential.Revoked, 401, "The device credential was revoked.");
    }
    private static string Hash(string ticket) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ticket)));
}
