using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Workflows;

namespace Jarvis.Orchestrator.Runtime;

public sealed record DeviceCommand(Guid Id, Guid GrantId, string DeviceId, string CapabilityId, string CapabilityVersion,
    string CredentialHash, string ArgumentsJson, string ArgumentHash, InvocationOrigin Origin, DeviceCommandStatus Status,
    DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? ConfirmedAt = null, string? ConfirmedBy = null,
    DateTimeOffset? CancelledAt = null, string? CancelledBy = null, Guid? DispatchRequestId = null, DateTimeOffset? DispatchedAt = null,
    string? HomeEntityId = null)
{
    public DeviceCommandStatus StatusAt(DateTimeOffset now) =>
        ExpiresAt <= now && Status is DeviceCommandStatus.Pending or DeviceCommandStatus.AwaitingConfirmation or
            DeviceCommandStatus.Dispatched or DeviceCommandStatus.Acknowledged
            ? DeviceCommandStatus.Expired : Status;

    public DeviceCommandView View(DateTimeOffset now) => new(Id, GrantId, DeviceId, CapabilityId, CapabilityVersion,
        ArgumentsJson, ArgumentHash, Origin.ToString(), StatusAt(now), CreatedAt, ExpiresAt, ConfirmedAt, ConfirmedBy,
        CancelledAt, CancelledBy, DispatchedAt);
}

public interface IDeviceCommandReader
{
    const int ReviewLimit = 50;
    Task<DeviceCommand?> GetAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<DeviceCommand>> ListForReviewAsync(DateTimeOffset now, CancellationToken ct = default);
}

public sealed class DeviceCommandService(IDeviceCapabilityCatalog capabilities, ICoreTransaction transaction, TimeProvider clock)
{
    public const string ConfirmScope = "device.commands.confirm";
    private static readonly TimeSpan MaximumLifetime = TimeSpan.FromMinutes(5);
    private const int HomeActionsPerMinute = 20;

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
            Guard.Require(HasActiveGrant(writes.Runtime, grant, deviceId, expiresAt, now), 403,
                "An active owner grant for this device and command lifetime is required.");
            var descriptor = capabilities.Get(grant.CapabilityId, grant.CapabilityVersion);
            Guard.Require(capabilities.Permits(grant.CapabilityId, grant.CapabilityVersion, grant.ConstraintsJson, request.ArgumentsJson),
                403, "Command arguments are outside the granted capability.");
            var homeEntity = BuiltInDeviceCapabilities.IsHomeAssistant(descriptor.Id, descriptor.Version) ? HomeEntity(request.ArgumentsJson) : null;
            Guard.Require(homeEntity is null || !AutomaticOrigin(origin) || !writes.Runtime.AgentControls.EffectivePaidAiPaused,
                409, "Home actions from voice and agents are paused.");
            var needsConfirmation = RequiresConfirmation(origin, descriptor, grant.PreApproved);
            var command = new DeviceCommand(request.RequestId, grant.Id, deviceId, grant.CapabilityId, grant.CapabilityVersion,
                grant.CredentialHash, request.ArgumentsJson, PrivacyData.Hash(request.ArgumentsJson), origin,
                needsConfirmation ? DeviceCommandStatus.AwaitingConfirmation : DeviceCommandStatus.Pending, Timestamp(now), expiresAt,
                HomeEntityId: homeEntity);
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
                Guard.Require(HasActiveGrant(writes.Runtime, grant, command.DeviceId, command.ExpiresAt, now) &&
                    MatchesGrantedArguments(command, grant),
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

    public Task<DeliveredDeviceCommand?> ClaimAsync(ClaimDeviceCommandRequest request, CancellationToken ct = default) =>
        ClaimAsync(request, homeAssistantOnly: false, ct);

    internal Task<bool> ConfirmGovernedAgentAsync(Guid id, CancellationToken ct) => transaction.ExecuteAsync(async writes => {
        var s = writes.Runtime;
        var p = s.AgentActions.GetValueOrDefault(id); var invocation = s.AgentToolInvocations.GetValueOrDefault(id);
        Guard.Require(CoreInvocationContext.Origin == InvocationOrigin.Agent && AgentToolExecutionScope.Contains(id, id) &&
            p is not null && AgentActionAuthority.Current(s, p, clock.GetUtcNow()) &&
            invocation is not null && invocation.Status == AgentToolStatus.Executing && invocation.TaskId == id &&
            invocation.GrantId == p.GrantId && invocation.CapabilityId == p.CapabilityId, 403, "Gateway action authorization is required.");
        var command = await writes.ReadDeviceCommandAsync(id);
        Guard.Require(command is not null && command.Status == DeviceCommandStatus.AwaitingConfirmation && command.Origin == InvocationOrigin.Agent &&
            command.GrantId == p.DeviceGrantId && command.DeviceId == p.DeviceId && command.ArgumentsJson == p.ArgumentsJson &&
            command.ArgumentHash == PrivacyData.Hash(p.ArgumentsJson), 409, "Device proposal changed.");
        var grant = await writes.ReadDeviceGrantAsync(p.DeviceGrantId);
        Guard.Require(HasActiveGrant(s, grant, p.DeviceId, command.ExpiresAt, clock.GetUtcNow()) && MatchesGrantedArguments(command, grant), 403, "Device grant changed.");
        var descriptor = capabilities.Get(command.CapabilityId, command.CapabilityVersion);
        var preapproved = descriptor.Risk == DeviceCapabilityRisk.Reversible && grant.PreApproved && s.AgentToolGrants[p.GrantId].PreApproved && !p.RequiresOwnerApproval;
        Guard.Require(preapproved || invocation.ApprovedBy is not null && invocation.ApprovedHash == invocation.ArgumentsHash,
            403, "Exact owner approval is required before action dispatch.");
        await writes.SaveDeviceCommandAsync(command with { Status = DeviceCommandStatus.Pending,
            ConfirmedAt = Timestamp(clock.GetUtcNow()), ConfirmedBy = invocation.ApprovedBy ?? "core:preapproved-agent-goal" }); return true;
    }, ct);

    public Task<DeliveredDeviceCommand?> ClaimHomeAssistantAsync(ClaimDeviceCommandRequest request, CancellationToken ct = default) =>
        ClaimAsync(request, homeAssistantOnly: true, ct);

    private Task<DeliveredDeviceCommand?> ClaimAsync(ClaimDeviceCommandRequest request, bool homeAssistantOnly, CancellationToken ct)
    {
        var actor = DeviceCredentialContext.RequirePairedActor();
        var deviceId = actor.DeviceId!;
        var credentialHash = actor.CredentialHash!;
        Guard.Require(request.RequestId != Guid.Empty, 400, "A dispatch request ID is required.");
        return transaction.ExecuteAsync<DeliveredDeviceCommand?>(async writes =>
        {
            DeviceCredentialContext.RequireCurrent(writes.Runtime, deviceId);
            var previous = await writes.ReadDeviceDispatchAsync(deviceId, credentialHash, request.RequestId);
            if (previous is not null)
            {
                if (previous.StatusAt(clock.GetUtcNow()) != DeviceCommandStatus.Dispatched || previous.DispatchedAt is null ||
                    (homeAssistantOnly && !IsHomeAssistant(previous)))
                    return null;
                var approved = await ReadDeliveryCapabilityAsync(writes, previous);
                return approved?.IdempotencyRule is DeviceIdempotencyRule.CallerKey or DeviceIdempotencyRule.SafeReplay
                    ? Delivery(previous) : null;
            }

            DeviceCommand? after = null;
            while (true)
            {
                var pending = await writes.ReadPendingDeviceCommandsAsync(deviceId, credentialHash, clock.GetUtcNow(), after);
                if (pending.Count == 0) return null;
                foreach (var command in pending)
                {
                    if (homeAssistantOnly && !IsHomeAssistant(command)) continue;
                    // Spoken actions without per-command confirmation are dispatched within the live tool call.
                    if (IsHomeAssistant(command) && command.Origin == InvocationOrigin.Voice && command.ConfirmedAt is null) continue;
                    var (delivery, _) = await TryDispatchAsync(writes, command, request.RequestId);
                    if (delivery is not null) return delivery;
                }
                after = pending[^1];
            }
        }, ct);
    }

    public async Task<DeliveredDeviceCommand?> ClaimVoiceHomeCommandAsync(Guid id, CancellationToken ct = default)
    {
        Guard.Require(CoreInvocationContext.Origin == InvocationOrigin.Voice, 403, "A voice window is required.");
        var actor = DeviceCredentialContext.RequirePairedActor();
        var (delivery, limited) = await transaction.ExecuteAsync<(DeliveredDeviceCommand? Delivery, bool Limited)>(async writes =>
        {
            DeviceCredentialContext.RequireCurrent(writes.Runtime, actor.DeviceId!);
            var command = await writes.ReadDeviceCommandAsync(id);
            if (command is null || command.Status != DeviceCommandStatus.Pending || command.Origin != InvocationOrigin.Voice ||
                command.DeviceId != actor.DeviceId || command.CredentialHash != actor.CredentialHash || !IsHomeAssistant(command) ||
                BuiltInDeviceCapabilities.IsSensitiveHomeAssistant(command.CapabilityId))
                return (null, false);
            var attempted = await TryDispatchAsync(writes, command, Guid.NewGuid());
            if (attempted.Limited)
                await writes.SaveDeviceCommandAsync(command with
                {
                    Status = DeviceCommandStatus.Cancelled, CancelledAt = Timestamp(clock.GetUtcNow()), CancelledBy = "core:home-rate-limit"
                });
            return attempted;
        }, ct);
        Guard.Require(!limited, 429, "The home action limit was reached for this device or entity.");
        return delivery;
    }

    private async Task<(DeliveredDeviceCommand? Delivery, bool Limited)> TryDispatchAsync(ICoreTransactionWrites writes,
        DeviceCommand command, Guid requestId)
    {
        if (await ReadDeliveryCapabilityAsync(writes, command) is null) return (null, false);
        var sensitive = BuiltInDeviceCapabilities.IsSensitiveHomeAssistant(command.CapabilityId);
        if (IsHomeAssistant(command) && (AutomaticOrigin(command.Origin) || sensitive) &&
            !await writes.HasHomeDispatchCapacityAsync(command.DeviceId, HomeEntity(command.ArgumentsJson),
                clock.GetUtcNow().AddMinutes(-1), HomeActionsPerMinute))
        {
            if (sensitive)
                await writes.SaveDeviceCommandAsync(command with
                {
                    Status = DeviceCommandStatus.Cancelled, CancelledAt = Timestamp(clock.GetUtcNow()), CancelledBy = "core:home-rate-limit"
                });
            return (null, true);
        }
        var now = clock.GetUtcNow();
        if (command.ExpiresAt <= now) return (null, false);
        var dispatched = Dispatch(command, requestId, now);
        await writes.SaveDeviceCommandAsync(dispatched);
        return (Delivery(dispatched), false);
    }

    public Task<DeliveredDeviceCommand> DispatchConfirmedHomeToolAsync(Guid toolId, BudgetActor? owner,
        IReadOnlyList<Guid> grantIds, CancellationToken ct = default)
    {
        Guard.Require(CoreInvocationContext.Origin == InvocationOrigin.ManualClient &&
            owner?.SubjectId is { Length: > 0 and <= 80 } subject && !string.IsNullOrWhiteSpace(subject) &&
            !subject.Contains('\0') && owner.Scopes.Contains(ToolService.ConfirmScope, StringComparer.Ordinal),
            403, "Manual owner authorization is required for the Home Assistant tool.");
        var actor = DeviceCredentialContext.RequirePairedActor();
        var deviceId = actor.DeviceId!;
        return transaction.ExecuteAsync(async writes =>
        {
            var state = writes.Runtime;
            DeviceCredentialContext.RequireCurrent(state, deviceId);
            Guard.Require(state.Tools.TryGetValue(toolId, out var tool) && tool.Invocation.ToolName == ToolService.HomeToolName &&
                tool.Invocation.Status == ToolInvocationStatus.Running && !tool.Invocation.TriggeredByExternalContent,
                409, "The Home Assistant tool is no longer approved for execution.");
            var sessionId = tool.Invocation.SessionId;
            var session = await writes.ReadAudioStateAsync(sessionId);
            Guard.Require(session?.Status == SessionStatus.Active && !state.RetiredSessions.Contains(sessionId),
                409, "Session is no longer active.");
            Guard.Require(await writes.ReadDeviceCommandAsync(toolId) is null, 409, "A device command already uses this tool ID.");

            var arguments = tool.Invocation.ArgumentsJson;
            foreach (var grantId in grantIds)
            {
                var grant = await writes.ReadDeviceGrantAsync(grantId);
                var now = clock.GetUtcNow();
                if (!HasActiveGrant(state, grant, deviceId, now, now) ||
                    grant.CapabilityId != BuiltInDeviceCapabilities.HomeAssistantTurn ||
                    grant.CapabilityVersion != BuiltInDeviceCapabilities.HomeAssistantTurnVersion ||
                    !capabilities.Permits(grant.CapabilityId, grant.CapabilityVersion, grant.ConstraintsJson, arguments))
                    continue;
                var expiresAt = Timestamp(grant.ExpiresAt < now.Add(MaximumLifetime) ? grant.ExpiresAt : now.Add(MaximumLifetime));
                if (expiresAt <= now) continue;
                var command = new DeviceCommand(toolId, grant.Id, deviceId, grant.CapabilityId, grant.CapabilityVersion,
                    grant.CredentialHash, arguments, PrivacyData.Hash(arguments), InvocationOrigin.ManualClient,
                    DeviceCommandStatus.Pending, Timestamp(now), expiresAt, Timestamp(now), owner!.SubjectId,
                    HomeEntityId: HomeEntity(arguments));
                var dispatched = Dispatch(command, Guid.NewGuid(), now);
                await writes.SaveDeviceCommandAsync(dispatched);
                return Delivery(dispatched);
            }
            throw new DomainException(403, "The Home Assistant executor needs an active owner grant for this entity.");
        }, ct);
    }

    public Task EnsureHomeAssistantExecutionAllowedAsync(DeliveredDeviceCommand delivery, CancellationToken ct = default)
    {
        var actor = DeviceCredentialContext.RequirePairedActor();
        return transaction.ExecuteAsync(async writes =>
        {
            DeviceCredentialContext.RequireCurrent(writes.Runtime, actor.DeviceId!);
            var command = await writes.ReadDeviceCommandAsync(delivery.Id);
            Guard.Require(command is not null && command.DeviceId == actor.DeviceId && command.CredentialHash == actor.CredentialHash &&
                command.DispatchRequestId == delivery.DispatchRequestId && command.Status == DeviceCommandStatus.Acknowledged &&
                command.ArgumentsJson == delivery.ArgumentsJson && IsHomeAssistant(command),
                409, "The Home Assistant dispatch is no longer available for execution.");
            Guard.Require(await ReadDeliveryCapabilityAsync(writes, command) is not null,
                403, "The Home Assistant dispatch expired or lost its owner grant.");
            return true;
        }, ct);
    }

    private async Task<DeviceCapabilityDescriptor?> ReadDeliveryCapabilityAsync(ICoreTransactionWrites writes, DeviceCommand command)
    {
        if (writes.Runtime.AgentActions.TryGetValue(command.Id, out var action) &&
            (!AgentActionAuthority.Current(writes.Runtime, action, clock.GetUtcNow()) ||
             writes.Runtime.AgentToolInvocations.GetValueOrDefault(command.Id)?.Status is not (AgentToolStatus.Succeeded or AgentToolStatus.Executing))) return null;
        if (IsHomeAssistant(command) && AutomaticOrigin(command.Origin) && writes.Runtime.AgentControls.EffectivePaidAiPaused)
            return null;
        var grant = await writes.ReadDeviceGrantAsync(command.GrantId);
        var now = clock.GetUtcNow();
        if (command.ExpiresAt <= now || !HasActiveGrant(writes.Runtime, grant, command.DeviceId, command.ExpiresAt, now) ||
            !MatchesGrantedArguments(command, grant) || PrivacyData.Hash(command.ArgumentsJson) != command.ArgumentHash)
            return null;
        var descriptor = capabilities.Get(command.CapabilityId, command.CapabilityVersion);
        return !RequiresConfirmation(command.Origin, descriptor, grant.PreApproved) ||
            command.ConfirmedAt is not null && !string.IsNullOrWhiteSpace(command.ConfirmedBy) ? descriptor : null;
    }

    private static bool IsHomeAssistant(DeviceCommand command) =>
        BuiltInDeviceCapabilities.IsHomeAssistant(command.CapabilityId, command.CapabilityVersion);

    private static string HomeEntity(string arguments)
    {
        using var json = JsonDocument.Parse(arguments);
        return json.RootElement.GetProperty("entityId").GetString()!;
    }

    private static DeviceCommand Dispatch(DeviceCommand command, Guid requestId, DateTimeOffset now) =>
        command with { Status = DeviceCommandStatus.Dispatched, DispatchRequestId = requestId, DispatchedAt = Timestamp(now) };

    private static DeliveredDeviceCommand Delivery(DeviceCommand command) =>
        new(command.Id, command.CapabilityId, command.CapabilityVersion, command.ArgumentsJson, command.ArgumentHash,
            command.ExpiresAt, command.DispatchedAt!.Value, command.DispatchRequestId!.Value);

    private bool MatchesGrantedArguments(DeviceCommand command, DeviceGrant grant) =>
        grant.CredentialHash == command.CredentialHash && grant.CapabilityId == command.CapabilityId &&
        grant.CapabilityVersion == command.CapabilityVersion &&
        capabilities.Permits(grant.CapabilityId, grant.CapabilityVersion, grant.ConstraintsJson, command.ArgumentsJson);

    private static bool AutomaticOrigin(InvocationOrigin origin) => origin is InvocationOrigin.Voice or InvocationOrigin.Agent;

    private static bool RequiresConfirmation(InvocationOrigin origin, DeviceCapabilityDescriptor descriptor, bool preApproved) =>
        !(preApproved && origin == InvocationOrigin.Voice && descriptor.Risk == DeviceCapabilityRisk.Reversible) &&
        (origin != InvocationOrigin.ManualClient || descriptor.ConfirmationPolicy == DeviceConfirmationPolicy.Owner ||
            descriptor.Risk is DeviceCapabilityRisk.Sensitive or DeviceCapabilityRisk.Irreversible);

    private static bool HasActiveGrant(RuntimeState state, [NotNullWhen(true)] DeviceGrant? grant,
        string deviceId, DateTimeOffset expiresAt, DateTimeOffset now) =>
        grant is not null && grant.DeviceId == deviceId && grant.Audit is not null &&
        grant.RevokedAt is null && grant.ExpiresAt > now && expiresAt <= grant.ExpiresAt &&
        state.Devices.ContainsKey(deviceId) &&
        state.DeviceCredentials.TryGetValue(deviceId, out var credential) && !credential.Revoked &&
        credential.SecretHash == grant.CredentialHash;

    private static DateTimeOffset Timestamp(DateTimeOffset value) =>
        new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);
}
