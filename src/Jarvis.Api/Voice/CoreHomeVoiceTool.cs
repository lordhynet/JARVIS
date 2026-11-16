using System.Text.Json;
using Jarvis.Api.Integrations;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Api.Voice;

public sealed class CoreHomeVoiceTool(IRuntimeReader runtime, DeviceGrantService grants, DeviceCommandService commands,
    IDeviceCommandReader commandReader, IDeviceReceiptReader receipts, HomeAssistantCommandAdapter adapter,
    ProviderGateway gateway, IConfiguration config)
{
    public const string Name = "core_home";
    private sealed record Target(string EntityId, string? FriendlyName, string[] Aliases, DeviceCapabilityGrantView Grant)
    {
        public string Label => Aliases.FirstOrDefault() ?? FriendlyName ?? EntityId;
        public bool Matches(string name) => Match(EntityId, name) || (FriendlyName is not null && Match(FriendlyName, name)) ||
            Aliases.Any(x => Match(x, name));
    }

    public async Task<object> RunAsync(string action, Guid sessionId, Guid source,
        Func<CancellationToken, Task<bool>>? currentAudio, CancellationToken ct)
    {
        Guard.Require(CoreInvocationContext.Origin == InvocationOrigin.Voice, 403, "An active voice window is required.");
        Guard.Require(action is "list" or "turn", 400, "The voice home tool supports list and turn.");
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
            var spoken = turn is null ? default : ParseTurn(turn.Text);
            var deviceId = await adapter.VoiceExecutorIdAsync(token);
            var targets = await TargetsAsync(deviceId, token);
            if (turn is null)
                return new { status = "available", entities = targets.Take(20).Select(t => new { t.EntityId, name = t.Label, t.Grant.PreApproved }), more = targets.Length > 20 };

            var matches = targets.Where(t => t.Matches(spoken.Name!)).ToArray();
            if (matches.Length > 1)
                return new { status = "ambiguous", candidates = matches.Take(20).Select(t => new { t.EntityId, name = t.Label }) };
            Guard.Require(matches.Length == 1, 403, "That light or switch is not in the effective Home Assistant grants.");
            var target = matches[0];
            var id = ManagedAi.Operation("voice-home-turn", sessionId, source);
            var arguments = JsonSerializer.Serialize(new { entityId = target.EntityId, action = spoken.Action });
            var expires = turn.At.AddMinutes(2);
            if (target.Grant.ExpiresAt < expires) expires = target.Grant.ExpiresAt;
            Guard.Require(currentAudio is not null && await currentAudio(token), 409, "The voice window no longer owns audio.");
            var proposal = await commands.ProposeAsync(deviceId, new(id, target.Grant.Id, arguments, expires), token);
            if (proposal.Status == DeviceCommandStatus.AwaitingConfirmation)
                return new { status = "awaiting_confirmation", commandId = id, name = target.Label };

            if (proposal.Status == DeviceCommandStatus.Pending)
                await adapter.ExecuteVoiceCommandAsync(id, currentAudio, token);
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

    private async Task<Target[]> TargetsAsync(string deviceId, CancellationToken ct)
    {
        var allowed = config.GetSection("HomeAssistant:AllowedEntities").Get<string[]>() ?? [];
        var aliases = config.GetSection("HomeAssistant:Aliases").GetChildren().ToArray();
        var rows = await grants.ListAsync(deviceId, ct);
        var entities = new Dictionary<string, DeviceCapabilityGrantView>(StringComparer.Ordinal);
        foreach (var grant in rows.Where(g => g.Effective && g.CapabilityId == BuiltInDeviceCapabilities.HomeAssistantTurn &&
            g.CapabilityVersion == BuiltInDeviceCapabilities.HomeAssistantTurnVersion)
            .OrderByDescending(g => g.PreApproved).ThenByDescending(g => g.ExpiresAt).ThenBy(g => g.Id))
        {
            using var constraints = JsonDocument.Parse(grant.ConstraintsJson);
            foreach (var item in constraints.RootElement.GetProperty("allowedEntityIds").EnumerateArray())
            {
                var entity = item.GetString()!;
                if (allowed.Contains(entity, StringComparer.Ordinal)) entities.TryAdd(entity, grant);
            }
        }
        Guard.Require(entities.Count <= 50, 409, "Voice home control supports up to 50 granted lights and switches per executor.");
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

    private static (string Name, string Action) ParseTurn(string text)
    {
        const string invalid = "Say a single request to turn a named light or switch on or off.";
        Guard.Require(text.Length is > 0 and <= 200 && !text.Any(char.IsControl), 400, invalid);
        var words = text.Trim().TrimEnd('.', '!').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var start = 0;
        var end = words.Length;
        bool Is(int index, string word) => index >= 0 && index < end && words[index].Equals(word, StringComparison.OrdinalIgnoreCase);
        bool Salutation(int index, string word) => index < end && words[index].TrimEnd(',').Equals(word, StringComparison.OrdinalIgnoreCase);
        if (Salutation(0, "hey") && Salutation(1, "jarvis")) start = 2;
        else if (Salutation(0, "jarvis")) start = 1;
        if (Is(start, "please")) start++;
        if (Is(end - 1, "please")) end--;
        Guard.Require(Is(start, "turn") && end - start >= 3, 400, invalid);
        start++;
        string? Direction(int index) => Is(index, "on") ? "turn_on" : Is(index, "off") ? "turn_off" : null;
        var action = Direction(start);
        if (action is not null) start++;
        else { action = Direction(end - 1); end--; }
        if (Is(start, "the")) start++;
        Guard.Require(action is not null && end > start, 400, invalid);
        var name = string.Join(' ', words[start..end]);
        Guard.Require(name.IndexOfAny([',', ';', '!', '?']) < 0, 400, invalid);
        return (name, action);
    }

    private static bool Match(string left, string right) =>
        string.Equals(string.Join(' ', left.Split(' ', StringSplitOptions.RemoveEmptyEntries)),
            string.Join(' ', right.Split(' ', StringSplitOptions.RemoveEmptyEntries)), StringComparison.OrdinalIgnoreCase);
}
