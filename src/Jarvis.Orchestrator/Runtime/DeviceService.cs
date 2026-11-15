using System.Text.RegularExpressions;
using Jarvis.Contracts;

namespace Jarvis.Orchestrator.Runtime;

public sealed class DeviceService(IRuntimeStore store, TimeProvider clock)
{
    public static readonly TimeSpan OnlineWindow = TimeSpan.FromSeconds(45);

    public Task<RegisteredDevice> RegisterAsync(RegisterDeviceRequest request, CancellationToken ct = default)
    {
        var id = Guard.Text(request.DeviceId, "DeviceId", 80);
        Guard.Require(Regex.IsMatch(id, "^[A-Za-z0-9_-]+$"), 400, "DeviceId contains invalid characters.");
        var name = Guard.Text(request.Name, "Name", 120);
        Guard.Require(Enum.IsDefined(request.Kind), 400, "Unknown device kind.");
        Guard.Require(request.Capabilities is not null && request.Capabilities.Extra is not null, 400, "Capabilities are required.");
        Guard.Require(request.Capabilities!.Extra.Count <= 20 && request.Capabilities.Extra.All(x => x is { Length: <= 80 }), 400, "Too many or invalid extra capabilities.");
        return store.UpdateAsync(s =>
        {
            var now = clock.GetUtcNow();
            Guard.Require(!s.DeviceCredentials.TryGetValue(id, out var credential) || credential.Revoked, 409,
                "An enrolled device cannot be overwritten. The owner must revoke its credential before changing registration.");
            s.Devices.TryGetValue(id, out var old);
            // Registration cannot self-certify hardware. Re-registering resets verification.
            var device = new RegisteredDevice(new(id, name, request.Kind, request.Capabilities,
                old?.Registration.RegisteredAt ?? now), now, false);
            s.Devices[id] = device;
            return device;
        }, ct);
    }

    public Task<RegisteredDevice> HeartbeatAsync(string id, CancellationToken ct = default) =>
        Change(id, d => d with { LastSeenAt = clock.GetUtcNow() }, ct);

    public Task<RegisteredDevice> VerifyAsync(string id, CancellationToken ct = default) =>
        Change(id, d => d with { CapabilitiesVerified = true }, ct);

    public async Task<RegisteredDevice> RequireOnlineAsync(string id, bool voice, CancellationToken ct)
    {
        var device = await store.ReadAsync(s => s.Devices.GetValueOrDefault(id), ct);
        Guard.Require(device is not null, 404, "Device not registered.");
        Guard.Require(IsOnline(device!), 409, "Device is offline. Send a heartbeat first.");
        Guard.Require(device!.CapabilitiesVerified, 409, "Verify the device capabilities first.");
        if (voice) Guard.Require(device.Registration.Capabilities.CanListen && device.Registration.Capabilities.CanSpeak,
            409, "Target device must support both listening and speaking.");
        return device;
    }

    private bool IsOnline(RegisteredDevice d) => clock.GetUtcNow() - d.LastSeenAt <= OnlineWindow;
    private Task<RegisteredDevice> Change(string id, Func<RegisteredDevice, RegisteredDevice> change, CancellationToken ct) =>
        store.UpdateAsync(s =>
        {
            DeviceCredentialContext.RequireCurrent(s, id);
            Guard.Require(s.Devices.TryGetValue(id, out var device), 404, "Device not registered.");
            var updated = change(device!);
            s.Devices[id] = updated;
            return updated;
        }, ct);
}
