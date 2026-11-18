using System.Text.Json;
using Jarvis.Api.Integrations;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Api.Voice;

public sealed class CoreHomeVoiceTool(IRuntimeReader runtime, DeviceGrantService grants, DeviceCommandService commands,
    IDeviceCommandReader commandReader, IDeviceReceiptReader receipts, HomeAssistantCommandAdapter adapter,
    ProviderGateway gateway, IDeviceCapabilityCatalog catalog, IConfiguration config)
{
    public const string Name = "core_home";
    private sealed record Target(string EntityId, string? FriendlyName, string[] Aliases, IReadOnlyList<DeviceCapabilityGrantView> Grants)
    {
        public string Label => Aliases.FirstOrDefault() ?? FriendlyName ?? EntityId;
        public bool Matches(string name) => Match(EntityId, name) || (FriendlyName is not null && Match(FriendlyName, name)) ||
            Aliases.Any(x => Match(x, name));
    }

    public async Task<object> RunAsync(string action, Guid sessionId, Guid source,
        Func<CancellationToken, Task<bool>>? currentAudio, CancellationToken ct)
    {
        Guard.Require(CoreInvocationContext.Origin == InvocationOrigin.Voice, 403, "An active voice window is required.");
        var capability = action == "list" ? null : HomeVoiceRequest.CapabilityFor(action);
        using var time = CancellationTokenSource.CreateLinkedTokenSource(ct);
        time.CancelAfter(TimeSpan.FromSeconds(12));
        try
        {
            var token = time.Token;
            var turn = await runtime.ReadAsync(state =>
            {
                Guard.Require(!state.AgentControls.EffectivePaidAiPaused, 409, "Home actions from voice are paused.");
                if (action == "list") return null;
                Guard.Require(state.VoiceTurns.TryGetValue(source, out var captured) &&
                    captured.SessionId == sessionId && captured.Role == ChatRoles.User, 409, "Wait for Core to capture the spoken request.");
                return captured;
            }, token);
            var spoken = turn is null ? null : HomeVoiceRequest.Parse(turn.Text, action);
            var deviceId = await adapter.VoiceExecutorIdAsync(token);
            var locked = await runtime.ReadAsync(s => VoiceAccessRules.Restricted(s, sessionId), token);
            var targets = await TargetsAsync(deviceId, capability, locked, token);
            if (turn is null || spoken is null)
                return new
                {
                    status = "available",
                    entities = targets.Take(20).Select(t => new
                    {
                        t.EntityId, name = t.Label,
                        preApproved = t.Grants.Any(g => g.CapabilityId == BuiltInDeviceCapabilities.HomeAssistantTurn && g.PreApproved),
                        capabilities = t.Grants.Select(g => g.CapabilityId).Distinct(StringComparer.Ordinal)
                    }),
                    more = targets.Length > 20
                };

            var matches = targets.Where(t => t.Matches(spoken.Name)).ToArray();
            if (matches.Length > 1)
                return new { status = "ambiguous", candidates = matches.Take(20).Select(t => new { t.EntityId, name = t.Label }) };
            Guard.Require(matches.Length == 1, 403, "That entity is not in the effective Home Assistant grants for this action.");
            var target = matches[0];
            var id = ManagedAi.Operation(action == "turn" ? "voice-home-turn" : "voice-home-action", sessionId, source);
            var arguments = spoken.Arguments(target.EntityId);
            var grant = target.Grants.FirstOrDefault(g => catalog.Permits(g.CapabilityId, g.CapabilityVersion, g.ConstraintsJson, arguments));
            Guard.Require(grant is not null, 403, "The requested setting is outside the owner's Home Assistant grant.");
            var expires = turn.At.AddMinutes(2);
            if (grant.ExpiresAt < expires) expires = grant.ExpiresAt;
            async Task<bool> AuthorizedAudio(CancellationToken check) => currentAudio is not null && await currentAudio(check) &&
                await runtime.ReadAsync(s => !VoiceAccessRules.Restricted(s, sessionId) || grant.AllowWhenLocked, check);
            Guard.Require(await AuthorizedAudio(token), 409, "The voice window no longer owns permitted audio.");
            var proposal = await commands.ProposeAsync(deviceId, new(id, grant.Id, arguments, expires), token);
            if (proposal.Status == DeviceCommandStatus.AwaitingConfirmation)
                return new { status = "awaiting_confirmation", commandId = id, name = target.Label,
                    detail = "The owner can review the exact request in the Core dashboard's Tools tab." };

            if (proposal.Status == DeviceCommandStatus.Pending && !BuiltInDeviceCapabilities.IsSensitiveHomeAssistant(spoken.Capability))
                await adapter.ExecuteVoiceCommandAsync(id, AuthorizedAudio, token);
            var stored = await commandReader.GetAsync(id, token);
            var recorded = await receipts.ListAsync(id, token);
            if (stored?.Status == DeviceCommandStatus.Succeeded && recorded.Any(x => x.Kind == DeviceReceiptKind.Succeeded && !x.Late))
                return new { status = "succeeded", commandId = id, name = target.Label, action = spoken.Action };
            return new { status = "unverified", commandId = id, detail = "Core has no verified success receipt for this command." };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new DomainException(502, "Home Assistant did not confirm the result before the voice tool timed out."); }
        catch (HttpRequestException)
        { throw new DomainException(502, "Home Assistant did not confirm the result. Check the device before trying again."); }
    }

    private async Task<Target[]> TargetsAsync(string deviceId, string? capability, bool locked, CancellationToken ct)
    {
        var allowed = config.GetSection("HomeAssistant:AllowedEntities").Get<string[]>() ?? [];
        var aliases = config.GetSection("HomeAssistant:Aliases").GetChildren().ToArray();
        var rows = await grants.ListAsync(deviceId, ct);
        var entities = new Dictionary<string, List<DeviceCapabilityGrantView>>(StringComparer.Ordinal);
        foreach (var grant in rows.Where(g => g.Effective && (!locked || g.AllowWhenLocked) && BuiltInDeviceCapabilities.IsHomeAssistant(g.CapabilityId, g.CapabilityVersion) &&
            (capability is null || g.CapabilityId == capability))
            .OrderByDescending(g => g.PreApproved).ThenByDescending(g => g.ExpiresAt).ThenBy(g => g.Id))
        {
            using var constraints = JsonDocument.Parse(grant.ConstraintsJson);
            foreach (var item in constraints.RootElement.GetProperty("allowedEntityIds").EnumerateArray())
            {
                var entity = item.GetString()!;
                if (!allowed.Contains(entity, StringComparer.Ordinal)) continue;
                if (!entities.TryGetValue(entity, out var entityGrants)) entities[entity] = entityGrants = [];
                entityGrants.Add(grant);
            }
        }
        Guard.Require(entities.Count <= 50, 409, "Voice home control supports up to 50 granted entities per lookup.");
        var entries = entities.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray();
        var targets = new Target[entries.Length];
        await Parallel.ForEachAsync(Enumerable.Range(0, entries.Length), new ParallelOptions { CancellationToken = ct, MaxDegreeOfParallelism = 4 },
            async (index, token) =>
            {
                var (entity, grant) = entries[index];
                var friendly = await gateway.HomeFriendlyNameAsync(entity, token);
                var names = aliases.Where(x => x.Value == entity && x.Key.Length is > 0 and <= 120).Select(x => x.Key).Take(10).ToArray();
                targets[index] = new(entity, friendly, names, grant);
            });
        return targets;
    }

    private static bool Match(string left, string right) =>
        string.Equals(string.Join(' ', left.Split(' ', StringSplitOptions.RemoveEmptyEntries)),
            string.Join(' ', right.Split(' ', StringSplitOptions.RemoveEmptyEntries)), StringComparison.OrdinalIgnoreCase);
}
