using System.Text.Json;
using Jarvis.Contracts;

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
    DateTimeOffset? RevokedAt = null,
    string? GrantedBy = null,
    string? Reason = null,
    bool PreApproved = false,
    bool AllowWhenLocked = false)
{
    public DeviceGrantAudit? Audit => GrantedBy is not null && Reason is not null ? new(Id, GrantedBy, Reason) : null;
    // Retain revoked audited requests until their original expiry so a retry cannot recreate them.
    public bool CanPrune(DateTimeOffset now) => ExpiresAt <= now || RevokedAt is not null && (GrantedBy is null || Reason is null);

    public DeviceGrant RequireSameRequest(DeviceGrant proposed)
    {
        Guard.Require(Audit is not null && GrantedBy == proposed.GrantedBy && Reason == proposed.Reason &&
            DeviceId == proposed.DeviceId && CapabilityId == proposed.CapabilityId && CapabilityVersion == proposed.CapabilityVersion &&
            CredentialHash == proposed.CredentialHash && ExpiresAt == proposed.ExpiresAt && PreApproved == proposed.PreApproved && AllowWhenLocked == proposed.AllowWhenLocked &&
            DeviceJsonSchema.TryReadObject(ConstraintsJson, 16_000, out var existing) &&
            DeviceJsonSchema.TryReadObject(proposed.ConstraintsJson, 8_000, out var requested) &&
            JsonElement.DeepEquals(existing, requested), 409, "Grant request ID was already used for different input.");
        return this;
    }
}

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

public sealed class DeviceGrantService(IRuntimeReader runtime, IDeviceCapabilityCatalog capabilities, IDeviceGrantStore grants,
    ICoreTransaction transaction, TimeProvider clock)
{
    public const string ManageScope = "device.grants";

    public async Task<DeviceCapabilityGrantView> GrantAsync(string deviceId, GrantDeviceCapabilityRequest request,
        BudgetActor actor, CancellationToken ct = default)
    {
        Guard.Require(actor.SubjectId is { Length: > 0 and <= 80 } && !string.IsNullOrWhiteSpace(actor.SubjectId) &&
            actor.Scopes.Contains(ManageScope, StringComparer.Ordinal), 403, "Owner grant authorization is required.");
        var descriptor = capabilities.Get(request.CapabilityId, request.CapabilityVersion);
        Guard.Require(!request.AllowWhenLocked || BuiltInDeviceCapabilities.IsHomeAssistant(descriptor.Id, descriptor.Version), 400,
            "Locked voice access can be granted only to Home Assistant capabilities.");
        Guard.Require(!request.PreApproved || descriptor.Risk == DeviceCapabilityRisk.Reversible, 400,
            "Only reversible capabilities can be pre-approved.");
        Guard.Require(deviceId is { Length: > 0 and <= 80 } && deviceId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'),
            400, "Device ID is invalid.");
        Guard.Require(request.RequestId != Guid.Empty, 400, "A grant request ID is required.");
        var reason = Guard.Text(request.Reason, "Grant reason", 1000);
        Guard.Require(!reason.Contains('\0') && !actor.SubjectId.Contains('\0'), 400, "Grant audit text contains an invalid character.");
        Guard.Require(capabilities.HasValidConstraints(descriptor.Id, descriptor.Version, request.ConstraintsJson),
            400, "Grant constraints must match the installed capability's non-empty allowlist schema.");
        var expiresAt = new DateTimeOffset(request.ExpiresAt.UtcTicks - request.ExpiresAt.UtcTicks % 10, TimeSpan.Zero);
        if (descriptor.Risk is DeviceCapabilityRisk.Sensitive or DeviceCapabilityRisk.Irreversible)
            Guard.Require(descriptor.ConfirmationPolicy == DeviceConfirmationPolicy.Owner, 409, "This capability has no safe owner-confirmation policy.");
        return await transaction.ExecuteAsync(async writes =>
        {
            var state = writes.Runtime;
            var now = clock.GetUtcNow();
            Guard.Require(expiresAt > now && expiresAt <= now.AddDays(365), 400, "Grant expiry must be within the next 365 days.");
            Guard.Require(state.Devices.ContainsKey(deviceId), 404, "Device is not registered.");
            Guard.Require(state.DeviceCredentials.TryGetValue(deviceId, out var credential) && !credential.Revoked, 409,
                "An active device credential is required before granting a capability.");
            var grant = await writes.AddDeviceGrantAsync(new(request.RequestId, deviceId, descriptor.Id, descriptor.Version,
                request.ConstraintsJson, credential.SecretHash, now, expiresAt, GrantedBy: actor.SubjectId, Reason: reason,
                PreApproved: request.PreApproved, AllowWhenLocked: request.AllowWhenLocked));
            return View(state, grant, now);
        }, ct);
    }

    public async Task<IReadOnlyList<DeviceCapabilityGrantView>> ListAsync(string? deviceId = null, CancellationToken ct = default) =>
        await ViewsAsync(await grants.ListAsync(deviceId, ct), ct);

    private Task<IReadOnlyList<DeviceCapabilityGrantView>> ViewsAsync(IReadOnlyList<DeviceGrant> rows, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return runtime.ReadAsync<IReadOnlyList<DeviceCapabilityGrantView>>(state => rows.Select(grant => View(state, grant, now)).ToArray(), ct);
    }

    private DeviceCapabilityGrantView View(RuntimeState state, DeviceGrant grant, DateTimeOffset now)
    {
        var audit = grant.Audit;
        var effective = audit is not null && grant.RevokedAt is null && grant.ExpiresAt > now &&
            state.Devices.ContainsKey(grant.DeviceId) &&
            state.DeviceCredentials.TryGetValue(grant.DeviceId, out var current) && !current.Revoked &&
            current.SecretHash == grant.CredentialHash &&
            capabilities.HasValidConstraints(grant.CapabilityId, grant.CapabilityVersion, grant.ConstraintsJson);
        return new(grant.Id, grant.DeviceId, grant.CapabilityId, grant.CapabilityVersion,
            grant.ConstraintsJson, grant.GrantedAt, grant.ExpiresAt, grant.RevokedAt, audit, effective, grant.PreApproved, grant.AllowWhenLocked);
    }

    public Task<bool> RevokeAsync(Guid id, CancellationToken ct = default) => grants.RevokeAsync(id, clock.GetUtcNow(), ct);
}
