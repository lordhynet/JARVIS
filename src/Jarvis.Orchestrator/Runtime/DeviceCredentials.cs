using System.Security.Cryptography;
using System.Text;

namespace Jarvis.Orchestrator.Runtime;

// Credential hashes are authentication material, excluded from personal-data exports.
public sealed record DeviceCredential(string DeviceId, string SecretHash, DateTimeOffset CreatedAt, bool Revoked = false);
public sealed record IssuedDeviceCredential(string DeviceId, string Secret);
public sealed record CoreActor(string SubjectId, string? DeviceId, string? CredentialHash = null);
public static class DeviceCredentialContext
{
    private static readonly AsyncLocal<CoreActor?> Current = new();
    public static IDisposable Enter(CoreActor actor) { var previous = Current.Value; Current.Value = actor; return new Scope(() => Current.Value = previous); }
    private sealed class Scope(Action close) : IDisposable { public void Dispose() => close(); }
    public static void RequireCurrent(RuntimeState state, string deviceId)
    {
        if (Current.Value is not { DeviceId: { } bound } actor) return;
        Guard.Require(bound == deviceId && state.DeviceCredentials.TryGetValue(bound, out var credential) &&
            !credential.Revoked && credential.SecretHash == actor.CredentialHash, 401, "The device credential changed or was revoked.");
    }
}

public sealed class DeviceCredentials(IRuntimeStore store, TimeProvider clock)
{
    public const string Prefix = "jdv1_";
    public Task<IssuedDeviceCredential> IssueAsync(string deviceId, CancellationToken ct) => store.UpdateAsync(s => {
        Guard.Require(s.Devices.ContainsKey(deviceId), 404, "Register the device before enrolling its credential.");
        Guard.Require(s.DeviceCredentials.ContainsKey(deviceId) || s.DeviceCredentials.Count < 100, 409, "Device credential capacity reached.");
        var secret = Prefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        s.DeviceCredentials[deviceId] = new(deviceId, PrivacyData.Hash(secret), clock.GetUtcNow());
        InvalidateAudio(s, deviceId);
        return new IssuedDeviceCredential(deviceId, secret);
    }, ct);

    public Task<bool> RevokeAsync(string deviceId, CancellationToken ct) => store.UpdateAsync(s => {
        if (!s.DeviceCredentials.TryGetValue(deviceId, out var credential)) return false;
        s.DeviceCredentials[deviceId] = credential with { Revoked = true };
        InvalidateAudio(s, deviceId); return true;
    }, ct);
    public async Task<CoreActor?> AuthenticateAsync(string secret, CancellationToken ct)
    {
        if (secret.Length != Prefix.Length + 64 || !secret.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        try { return await store.ReadAsync(s => {
            var found = s.DeviceCredentials.Values.FirstOrDefault(c => !c.Revoked &&
                CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(c.SecretHash)));
            return found is null ? null : new CoreActor("device:" + found.DeviceId, found.DeviceId, found.SecretHash);
        }, ct); }
        // Erasure also destroys device credentials. Return an authentication failure so
        // the phone invalidates its connection instead of treating erasure as an outage.
        catch (DomainException e) when (e.StatusCode == 503) { return null; }
    }
    private static void InvalidateAudio(RuntimeState s, string deviceId)
    {
        if (s.AudioLease?.DeviceId == deviceId) AudioFencing.Revoke(s);
        foreach (var key in s.VoiceTickets.Where(x => x.Value.Lease.DeviceId == deviceId).Select(x => x.Key).ToList()) s.VoiceTickets.Remove(key);
    }
}
