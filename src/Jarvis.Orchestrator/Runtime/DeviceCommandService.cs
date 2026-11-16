using System.Diagnostics.CodeAnalysis;
using Jarvis.Contracts;

namespace Jarvis.Orchestrator.Runtime;

public sealed record DeviceCommand(Guid Id, Guid GrantId, string DeviceId, string CapabilityId, string CapabilityVersion,
    string CredentialHash, string ArgumentsJson, string ArgumentHash, InvocationOrigin Origin, DeviceCommandStatus Status,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? ConfirmedAt = null, string? ConfirmedBy = null,
    DateTimeOffset? CancelledAt = null, string? CancelledBy = null)
{
    public DeviceCommandStatus StatusAt(DateTimeOffset now) =>
        ExpiresAt <= now && Status is DeviceCommandStatus.Pending or DeviceCommandStatus.AwaitingConfirmation
            ? DeviceCommandStatus.Expired : Status;

    public DeviceCommandView View(DateTimeOffset now) => new(Id, GrantId, DeviceId, CapabilityId, CapabilityVersion,
        ArgumentsJson, ArgumentHash, Origin.ToString(), StatusAt(now), CreatedAt, ExpiresAt, ConfirmedAt, ConfirmedBy,
        CancelledAt, CancelledBy);
}

public interface IDeviceCommandReader
{
    Task<DeviceCommand?> GetAsync(Guid id, CancellationToken ct = default);
}

public sealed class DeviceCommandService(IDeviceCapabilityCatalog capabilities, ICoreTransaction transaction, TimeProvider clock)
{
    public const string ConfirmScope = "device.commands.confirm";
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(5);

    public Task<DeviceCommandView> ProposeAsync(string deviceId, ProposeDeviceCommandRequest request, CancellationToken ct = default)
    {
        Guard.Require(request.RequestId != Guid.Empty && request.GrantId != Guid.Empty, 400, "Command and grant request IDs are required.");
        Guard.Require(deviceId is { Length: > 0 and <= 80 } && deviceId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'),
            400, "Device ID is invalid.");
        Guard.Require(DeviceJsonSchema.TryReadObject(request.ArgumentsJson, 8_000, out _), 400, "Command arguments must be a valid JSON object.");
        var expiresAt = Timestamp(request.ExpiresAt);
        var origin = CoreInvocationContext.Origin;
        return transaction.ExecuteAsync(async writes =>
        {
            var existing = await writes.ReadDeviceCommandAsync(request.RequestId);
            if (existing is not null)
            {
                Guard.Require(existing.DeviceId == deviceId && existing.GrantId == request.GrantId &&
                    existing.ArgumentsJson == request.ArgumentsJson && existing.ExpiresAt == expiresAt && existing.Origin == origin,
                    409, "Command request ID was already used for different input.");
                return existing.View(clock.GetUtcNow());
            }

            var grant = await writes.ReadDeviceGrantAsync(request.GrantId);
            var now = clock.GetUtcNow();
            Guard.Require(expiresAt > now && expiresAt <= now.Add(MaximumLifetime), 400, "Command expiry must be within the next five minutes.");
            RequireActiveGrant(writes.Runtime, grant, deviceId, expiresAt, now);
            var descriptor = capabilities.Get(grant.CapabilityId, grant.CapabilityVersion);
            Guard.Require(capabilities.Permits(grant.CapabilityId, grant.CapabilityVersion, grant.ConstraintsJson, request.ArgumentsJson),
                403, "Command arguments are outside the granted capability.");
            var needsConfirmation = origin != InvocationOrigin.ManualClient || descriptor.ConfirmationPolicy == DeviceConfirmationPolicy.Owner ||
                descriptor.Risk is DeviceCapabilityRisk.Sensitive or DeviceCapabilityRisk.Irreversible;
            var command = new DeviceCommand(request.RequestId, grant.Id, deviceId, grant.CapabilityId, grant.CapabilityVersion,
                grant.CredentialHash, request.ArgumentsJson, PrivacyData.Hash(request.ArgumentsJson), origin,
                needsConfirmation ? DeviceCommandStatus.AwaitingConfirmation : DeviceCommandStatus.Pending, Timestamp(now), expiresAt);
            await writes.SaveDeviceCommandAsync(command);
            return command.View(now);
        }, ct);
    }

    public Task<DeviceCommandView> ConfirmAsync(Guid id, ConfirmDeviceCommandRequest request, BudgetActor actor,
        CancellationToken ct = default)
    {
        Guard.Require(CoreInvocationContext.Origin == InvocationOrigin.ManualClient &&
            actor.SubjectId is { Length: > 0 and <= 80 } && !string.IsNullOrWhiteSpace(actor.SubjectId) &&
            !actor.SubjectId.Contains('\0') && actor.Scopes.Contains(ConfirmScope, StringComparer.Ordinal),
            403, "Manual owner command confirmation is required.");
        Guard.Require(request.Approve is not null, 400, "An explicit command decision is required.");
        var approve = request.Approve.Value;
        return transaction.ExecuteAsync(async writes =>
        {
            var command = await writes.ReadDeviceCommandAsync(id);
            Guard.Require(command is not null, 404, "Device command was not found.");
            Guard.Require(request.ArgumentHash == command.ArgumentHash && PrivacyData.Hash(command.ArgumentsJson) == command.ArgumentHash,
                409, "Command argument hash does not match.");
            var now = clock.GetUtcNow();
            if (command.StatusAt(now) == DeviceCommandStatus.Expired)
            {
                if (command.Status != DeviceCommandStatus.Expired)
                    await writes.SaveDeviceCommandAsync(command with { Status = DeviceCommandStatus.Expired });
                return command.View(now);
            }

            if (approve && command.ConfirmedAt is not null &&
                command.Status is not DeviceCommandStatus.Cancelled)
                return command.View(now);
            if (!approve && command.Status == DeviceCommandStatus.Cancelled)
                return command.View(now);

            Guard.Require(command.Status == DeviceCommandStatus.AwaitingConfirmation ||
                !approve && command.Status == DeviceCommandStatus.Pending, 409, "This command no longer accepts this decision.");
            if (approve)
            {
                var grant = await writes.ReadDeviceGrantAsync(command.GrantId);
                now = clock.GetUtcNow();
                if (command.ExpiresAt <= now)
                {
                    await writes.SaveDeviceCommandAsync(command with { Status = DeviceCommandStatus.Expired });
                    return command.View(now);
                }
                RequireActiveGrant(writes.Runtime, grant, command.DeviceId, command.ExpiresAt, now);
                Guard.Require(grant.CredentialHash == command.CredentialHash && grant.CapabilityId == command.CapabilityId &&
                    grant.CapabilityVersion == command.CapabilityVersion &&
                    capabilities.Permits(grant.CapabilityId, grant.CapabilityVersion, grant.ConstraintsJson, command.ArgumentsJson),
                    403, "The command's granted capability is no longer valid.");
                command = command with { Status = DeviceCommandStatus.Pending, ConfirmedAt = Timestamp(now), ConfirmedBy = actor.SubjectId };
            }
            else
            {
                command = command with { Status = DeviceCommandStatus.Cancelled, CancelledAt = Timestamp(now), CancelledBy = actor.SubjectId };
            }
            await writes.SaveDeviceCommandAsync(command);
            return command.View(now);
        }, ct);
    }

    private static void RequireActiveGrant(RuntimeState state, [NotNull] DeviceGrant? grant,
        string deviceId, DateTimeOffset expiresAt, DateTimeOffset now)
    {
        Guard.Require(grant is not null && grant.DeviceId == deviceId && grant.Audit is not null &&
            grant.RevokedAt is null && grant.ExpiresAt > now && expiresAt <= grant.ExpiresAt &&
            state.Devices.ContainsKey(deviceId) &&
            state.DeviceCredentials.TryGetValue(deviceId, out var credential) && !credential.Revoked &&
            credential.SecretHash == grant.CredentialHash, 403, "An active owner grant for this device and command lifetime is required.");
    }

    private static DateTimeOffset Timestamp(DateTimeOffset value) =>
        new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);
}
