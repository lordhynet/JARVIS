using System.Text.Json;

namespace Jarvis.Orchestrator.Runtime;

public sealed record DeviceGrant(
    Guid Id,
    string DeviceId,
    string CapabilityId,
    string CapabilityVersion,
    string ConstraintsJson,
    string CredentialHash,
    DateTimeOffset GrantedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RevokedAt = null);

public interface IDeviceGrantStore
{
    Task<DeviceGrant> AddAsync(DeviceGrant grant, CancellationToken ct = default);
    Task<DeviceGrant?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<DeviceGrant>> ListAsync(string? deviceId = null, CancellationToken ct = default);
    Task<DeviceGrant?> FindActiveAsync(string deviceId, string capabilityId, string capabilityVersion, string credentialHash, DateTimeOffset now, CancellationToken ct = default);
    Task<bool> RevokeAsync(Guid id, DateTimeOffset now, CancellationToken ct = default);
    Task<int> RevokeForDeviceAsync(string deviceId, DateTimeOffset now, CancellationToken ct = default);
    Task<int> PruneAsync(DateTimeOffset now, CancellationToken ct = default);
}

public sealed class DeviceGrantService(IRuntimeReader runtime, IDeviceCapabilityCatalog capabilities, IDeviceGrantStore grants, TimeProvider clock)
{
    public async Task<DeviceGrant> GrantAsync(string deviceId, string capabilityId, string version, string constraintsJson,
        DateTimeOffset expiresAt, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var descriptor = capabilities.Get(capabilityId, version);
        Guard.Require(deviceId is { Length: > 0 and <= 80 } && deviceId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'),
            400, "Device ID is invalid.");
        Guard.Require(constraintsJson is { Length: > 0 and <= 8_000 }, 400, "Grant constraints must be bounded JSON.");
        try
        {
            using var constraints = JsonDocument.Parse(constraintsJson);
            Guard.Require(constraints.RootElement.ValueKind == JsonValueKind.Object, 400, "Grant constraints must be a JSON object.");
        }
        catch (JsonException) { throw new DomainException(400, "Grant constraints must be valid JSON."); }
        expiresAt = expiresAt.ToUniversalTime();
        Guard.Require(expiresAt > now && expiresAt <= now.AddDays(365), 400, "Grant expiry must be within the next 365 days.");
        if (descriptor.Risk is DeviceCapabilityRisk.Sensitive or DeviceCapabilityRisk.Irreversible)
            Guard.Require(descriptor.ConfirmationPolicy == DeviceConfirmationPolicy.Owner, 409, "This capability has no safe owner-confirmation policy.");
        var binding = await runtime.ReadAsync(state =>
        {
            Guard.Require(state.Devices.ContainsKey(deviceId), 404, "Device is not registered.");
            Guard.Require(state.DeviceCredentials.TryGetValue(deviceId, out var credential) && !credential.Revoked, 409,
                "An active device credential is required before granting a capability.");
            return state.DeviceCredentials[deviceId].SecretHash;
        }, ct);
        return await grants.AddAsync(new(Guid.NewGuid(), deviceId, capabilityId, version, constraintsJson, binding, now, expiresAt), ct);
    }

    public Task<IReadOnlyList<DeviceGrant>> ListAsync(string? deviceId = null, CancellationToken ct = default) => grants.ListAsync(deviceId, ct);
    public Task<bool> RevokeAsync(Guid id, CancellationToken ct = default) => grants.RevokeAsync(id, clock.GetUtcNow(), ct);
}
