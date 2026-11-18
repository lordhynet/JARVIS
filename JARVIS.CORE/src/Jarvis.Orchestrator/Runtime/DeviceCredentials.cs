using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Jarvis.Contracts;

namespace Jarvis.Orchestrator.Runtime;

// Credential hashes are authentication material, excluded from personal-data exports.
public sealed record OwnerElevationRecord(string TokenHash, long Epoch, DateTimeOffset ExpiresAt, IReadOnlyList<string> Scopes, string OwnerCredentialHash);
public sealed record DeviceCredential(string DeviceId, string SecretHash, DateTimeOffset CreatedAt, bool Revoked = false,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Purpose = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] OwnerElevationRecord? Elevation = null);
public sealed record IssuedDeviceCredential(string DeviceId, string Secret);
public sealed record CoreActor(string SubjectId, string? DeviceId, string? CredentialHash = null, bool IsOwnerBrowser = false);
public static class DeviceCredentialContext
{
    private static readonly AsyncLocal<CoreActor?> Current = new();
    public static CoreActor? CurrentActor => Current.Value;
    public static IDisposable Enter(CoreActor actor) { var previous = Current.Value; Current.Value = actor; return new Scope(() => Current.Value = previous); }
    private sealed class Scope(Action close) : IDisposable { public void Dispose() => close(); }
    public static CoreActor RequirePairedActor()
    {
        var actor = Current.Value;
        Guard.Require(actor is { DeviceId: not null, CredentialHash: not null }, 403, "A paired device credential is required.");
        return actor;
    }
    public static void RequireCurrent(RuntimeState state, string deviceId)
    {
        if (Current.Value is not { DeviceId: { } bound } actor) return;
        Guard.Require((bound == deviceId || actor.IsOwnerBrowser) && state.DeviceCredentials.TryGetValue(bound, out var credential) &&
            !credential.Revoked && credential.SecretHash == actor.CredentialHash &&
            (!actor.IsOwnerBrowser || credential.Purpose == DeviceCredentials.OwnerBrowserPurpose), 401, "The device credential changed or was revoked.");
    }
}

public sealed class DeviceCredentials(IRuntimeReader store, ICoreTransaction transaction, TimeProvider clock)
{
    public const string Prefix = "jdv1_";
    public const string OwnerBrowserPurpose = "owner-browser";
    public Task<IssuedDeviceCredential> IssueAsync(string deviceId, CancellationToken ct) => IssueCoreAsync(deviceId, ct);
    public Task<IssuedDeviceCredential> EnrolBrowserAsync(RegisterDeviceRequest request, CancellationToken ct)
    {
        Guard.Require(CoreInvocationContext.Origin == InvocationOrigin.ManualClient, 403, "Browser enrollment requires a manual owner client.");
        var id = Guard.Text(request.DeviceId, "DeviceId", 80); Guard.Text(request.Name, "Name", 120);
        Guard.Require(Regex.IsMatch(id, "^[A-Za-z0-9_-]+$") && request.Kind is DeviceKind.WindowsAgent or DeviceKind.AndroidCompanion or DeviceKind.QuestClient &&
            request.Capabilities is { HasDisplay: true }, 400, "Enroll an interactive browser with a valid device identity.");
        return IssueCoreAsync(id, ct, request);
    }
    private Task<IssuedDeviceCredential> IssueCoreAsync(string deviceId, CancellationToken ct, RegisterDeviceRequest? browser = null) =>
        transaction.ExecuteAsync(async writes =>
        {
            var state = writes.Runtime;
            var now = clock.GetUtcNow();
            if (browser is not null) {
                Guard.Require(!state.DeviceCredentials.TryGetValue(deviceId, out var old) || old.Revoked, 409,
                    "This browser already has an active credential. Reconnect with it or revoke it before enrolling again.");
                var capabilities = browser.Capabilities with { CanControlHome = false, Extra = [VoiceAccessRules.BrowserCompanionTag] };
                state.Devices[deviceId] = new(new(deviceId, browser.Name.Trim(), browser.Kind, capabilities, now), now, false);
            }
            Guard.Require(state.Devices.ContainsKey(deviceId), 404, "Register the device before enrolling its credential.");
            Guard.Require(state.DeviceCredentials.ContainsKey(deviceId) || state.DeviceCredentials.Count < 100, 409, "Device credential capacity reached.");
            var secret = Prefix + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            state.DeviceCredentials[deviceId] = new(deviceId, PrivacyData.Hash(secret), now, Purpose: browser is null ? null : OwnerBrowserPurpose);
            InvalidateAudio(state, deviceId);
            await writes.RevokeDeviceGrantsAsync(deviceId, now);
            return new IssuedDeviceCredential(deviceId, secret);
        }, ct);

    public Task<bool> RevokeAsync(string deviceId, CancellationToken ct) =>
        transaction.ExecuteAsync(async writes =>
        {
            var state = writes.Runtime;
            if (!state.DeviceCredentials.TryGetValue(deviceId, out var credential)) return false;
            state.DeviceCredentials[deviceId] = credential with { Revoked = true };
            InvalidateAudio(state, deviceId);
            await writes.RevokeDeviceGrantsAsync(deviceId, clock.GetUtcNow());
            return true;
        }, ct);
    public async Task<CoreActor?> AuthenticateAsync(string secret, CancellationToken ct)
    {
        if (secret.Length != Prefix.Length + 64 || !secret.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(secret));
        try { return await store.ReadAsync(s => {
            var found = s.DeviceCredentials.Values.FirstOrDefault(c => !c.Revoked &&
                CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(c.SecretHash)));
            return found is null ? null : new CoreActor("device:" + found.DeviceId, found.DeviceId, found.SecretHash, found.Purpose == OwnerBrowserPurpose);
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
