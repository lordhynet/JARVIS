using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Jarvis.Contracts;

namespace Jarvis.Orchestrator.Runtime;

public sealed record DevicePairingCode(string Hash, string? DeviceId, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);
public sealed record CreatePairingCodeRequest(string? DeviceId);
public sealed record IssuedPairingCode(string Code, DateTimeOffset ExpiresAt, string? DeviceId);
public sealed record PairingExchangeRequest(string Code, string DeviceId, string? Name);

// QR pairing for the Android audio companion. The owner mints a single-use, five-minute code; the phone
// exchanges it for its own scoped device credential. The shared Core key never leaves the owner's computer.
public sealed partial class DevicePairing(IRuntimeStore store, DeviceCredentials credentials, TimeProvider clock)
{
    public const string Prefix = "jpc1_";
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);
    public const int MaximumActive = 10;

    public Task<IssuedPairingCode> CreateAsync(CreatePairingCodeRequest request, CancellationToken ct = default)
    {
        Guard.Require(CoreInvocationContext.Origin == InvocationOrigin.ManualClient, 403, "Only the owner can create a pairing code.");
        var target = request.DeviceId is null ? null : Guard.Text(request.DeviceId, "DeviceId", 80);
        var code = Prefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        return store.UpdateAsync(s =>
        {
            var now = clock.GetUtcNow();
            Prune(s, now);
            if (target is not null)
                Guard.Require(s.Devices.TryGetValue(target, out var device) && IsAudioCompanion(device.Registration), 409,
                    "Only a registered Android audio companion can be paired again.");
            Guard.Require(s.DevicePairingCodes.Count < MaximumActive, 429, "Too many active pairing codes. Wait five minutes.");
            var record = new DevicePairingCode(PrivacyData.Hash(code), target, now, now.Add(Lifetime));
            s.DevicePairingCodes[record.Hash] = record;
            return new IssuedPairingCode(code, record.ExpiresAt, target);
        }, ct);
    }

    public async Task<IssuedDeviceCredential> ExchangeAsync(PairingExchangeRequest request, CancellationToken ct = default)
    {
        Guard.Require(request is not null && CodePattern().IsMatch(request.Code ?? ""), 400, "The pairing code is invalid.");
        var phoneId = Guard.Text(request!.DeviceId, "DeviceId", 80);
        Guard.Require(PhonePattern().IsMatch(phoneId), 400, "The phone identity is invalid.");
        var name = string.IsNullOrWhiteSpace(request.Name) ? "Android phone" : Guard.Text(request.Name.Trim(), "Name", 120);
        Guard.Require(!name.Any(char.IsControl), 400, "The phone name is invalid.");
        var hash = PrivacyData.Hash(request.Code!);
        var deviceId = await store.UpdateAsync(s =>
        {
            var now = clock.GetUtcNow();
            // Consume first: a wrong, expired or reused code never reaches registration.
            var found = s.DevicePairingCodes.Remove(hash, out var record);
            Prune(s, now);
            Guard.Require(found && record!.ExpiresAt > now, 401, "This pairing code has expired or was already used. Show a new code on your computer.");
            var id = record!.DeviceId ?? phoneId;
            if (s.Devices.TryGetValue(id, out var existing))
            {
                Guard.Require(IsAudioCompanion(existing.Registration), 409, "That identity belongs to another kind of device.");
                Guard.Require(record.DeviceId is not null || !s.DeviceCredentials.TryGetValue(id, out var credential) || credential.Revoked, 409,
                    "This phone is already paired. Use Pair again on its row in Devices.");
            }
            else
            {
                Guard.Require(record.DeviceId is null, 409, "The device for this pairing code was removed.");
                s.Devices[id] = new(new(id, name, DeviceKind.AndroidCompanion,
                    new(true, true, false, false, ["native-android", "audio-only"]), now), now, false);
            }
            return id;
        }, ct);
        // Issuing replaces any previous credential for this device and revokes its grants.
        return await credentials.IssueAsync(deviceId, ct);
    }

    private static bool IsAudioCompanion(DeviceRegistration registration) => registration.Kind == DeviceKind.AndroidCompanion &&
        registration.Capabilities.Extra?.Contains("audio-only") == true && !registration.Capabilities.HasDisplay;

    private static void Prune(RuntimeState state, DateTimeOffset now)
    {
        foreach (var stale in state.DevicePairingCodes.Where(x => x.Value.ExpiresAt <= now).Select(x => x.Key).ToArray())
            state.DevicePairingCodes.Remove(stale);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [GeneratedRegex("^jpc1_[A-Za-z0-9_-]{43}$")] private static partial Regex CodePattern();
    [GeneratedRegex("^android-[A-Za-z0-9-]{8,71}$")] private static partial Regex PhonePattern();
}
