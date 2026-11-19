using System.Text.Json;
using System.Text.RegularExpressions;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;

namespace Jarvis.Api.Voice;

public sealed class CoreWindowsVoiceTool(IRuntimeStore store, DeviceGrantService grants, DeviceCommandService commands,
    IDeviceReceiptReader receipts, IDeviceCapabilityCatalog capabilities, TimeProvider clock, IConfiguration configuration)
{
    public const string Name = "core_windows";
    public async Task<object> RunAsync(string action, Guid session, Guid source, Func<CancellationToken, Task<bool>>? audio, CancellationToken ct)
    {
        Guard.Require(CoreInvocationContext.Origin == InvocationOrigin.Voice && audio is not null && await audio(ct), 403, "A live Core voice window is required.");
        var candidates = await store.ReadAsync(s => {
            Guard.Require(!VoiceAccessRules.ToolRestricted(s, session) || s.AudioLease?.SessionId == session, 403, "Current Core audio is required.");
            var native = s.Devices.Values.Where(d => d.Registration.Kind == DeviceKind.WindowsAgent && d.Registration.Capabilities.Extra.Contains(CompanionVoiceProtocol.NativeWindows)).Select(d => d.Registration.DeviceId).ToArray();
            var preferred = configuration["WindowsActions:VoiceDeviceId"];
            if (!string.IsNullOrWhiteSpace(preferred)) return native.Where(d => d == preferred).ToArray();
            return s.AudioLease is { } lease && native.Contains(lease.DeviceId) ? [lease.DeviceId] : native;
        }, ct);
        Guard.Require(candidates.Length == 1, 409, "Choose one enrolled Windows voice target in Core configuration.");
        var rows = (await grants.ListAsync(candidates[0], ct)).Where(g => g.Effective && WindowsDeviceCapabilities.Installed(g.CapabilityId, g.CapabilityVersion)).ToArray();
        var locked = await store.ReadAsync(s => VoiceAccessRules.ToolRestricted(s, session), ct);
        if (action == "list") return new { status = "available", capabilities = rows.Where(g => !locked || g.AllowWhenLocked && WindowsCommandProtocol.AllowedLocked(g.CapabilityId))
            .Take(10).Select(g => new { g.CapabilityId, constraints = locked ? null : g.ConstraintsJson }) };
        Guard.Require(action == "control", 400, "Unknown laptop voice action.");
        var turn = await store.ReadAsync(s => {
            Guard.Require(s.VoiceTurns.TryGetValue(source, out var captured) && captured.SessionId == session && captured.Role == ChatRoles.User &&
                captured.At >= clock.GetUtcNow().AddSeconds(-90) && !s.RetiredSessions.Contains(session), 409, "Use the latest captured laptop request."); return captured;
        }, ct);
        var (capability, arguments) = Parse(turn.Text);
        Guard.Require(!locked && !turn.Restricted || WindowsCommandProtocol.AllowedLocked(capability), 403, "Only media and lock requests are available while locked.");
        var grant = rows.Where(g => g.CapabilityId == capability && (!locked && !turn.Restricted || g.AllowWhenLocked) && capabilities.Permits(g.CapabilityId, g.CapabilityVersion, g.ConstraintsJson, arguments))
            .OrderByDescending(g => g.PreApproved).ThenByDescending(g => g.ExpiresAt).FirstOrDefault();
        Guard.Require(grant is not null, 403, "No current Windows grant permits the captured request.");
        var id = AgentRuntime.StableId("voice-windows-v1:" + session + ":" + source);
        var expires = new[] { turn.At.AddMinutes(2), grant.ExpiresAt }.Min();
        await store.UpdateAsync(s => {
            var lease = s.AudioLease;
            Guard.Require(lease is not null && lease.SessionId == session && lease.ExpiresAt > clock.GetUtcNow() &&
                (!lease.Locked || grant.AllowWhenLocked && WindowsCommandProtocol.AllowedLocked(capability)), 403, "Permitted audio ownership changed.");
            foreach (var old in s.DeviceVoiceAuthorities.Where(p => p.Value.ExpiresAt <= clock.GetUtcNow() || s.RetiredSessions.Contains(p.Value.SessionId)).Select(p => p.Key).ToArray()) s.DeviceVoiceAuthorities.Remove(old);
            Guard.Require(s.DeviceVoiceAuthorities.Count < 4096 || s.DeviceVoiceAuthorities.ContainsKey(id), 409, "Voice command capacity reached.");
            var authority = new DeviceVoiceAuthority(session, source, lease.Fence, expires);
            if (s.DeviceVoiceAuthorities.TryGetValue(id, out var prior)) Guard.Require(prior == authority, 409, "Captured request belongs to an earlier audio fence.");
            s.DeviceVoiceAuthorities[id] = authority; return true;
        }, ct);
        Guard.Require(await audio(ct), 403, "Audio ownership changed.");
        var proposal = await commands.ProposeAsync(candidates[0], new(id, grant.Id, arguments, expires), ct);
        if (proposal.Status == DeviceCommandStatus.AwaitingConfirmation) return new { status = "awaiting_confirmation", commandId = id, detail = "Review the exact laptop request in the dashboard Tools tab." };
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct); wait.CancelAfter(TimeSpan.FromSeconds(5));
        try {
            while (await audio(wait.Token)) {
                var recorded = await receipts.ListAsync(id, wait.Token);
                var verified = recorded.FirstOrDefault(r => r.Kind == DeviceReceiptKind.Succeeded && !r.Late);
                if (verified is not null) {
                    Guard.Require(await store.ReadAsync(s => s.DeviceVoiceAuthorities.TryGetValue(id, out var authority) &&
                        s.AudioLease is { } lease && lease.SessionId == session && lease.Fence == authority.AudioFence && lease.ExpiresAt > clock.GetUtcNow() &&
                        (!lease.Locked || grant.AllowWhenLocked && WindowsCommandProtocol.AllowedLocked(capability)), ct), 403, "Private laptop results are unavailable after audio ownership or lock state changes.");
                    return new { status = "succeeded", commandId = id, receipt = JsonSerializer.Deserialize<JsonElement>(verified.PayloadJson) };
                }
                if (recorded.Any(r => r.Kind == DeviceReceiptKind.Failed)) break;
                await Task.Delay(250, wait.Token);
            }
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        return new { status = "unverified", commandId = id, detail = "No verified laptop completion receipt is available. Check the device before retrying." };
    }
    public (string Capability, string Arguments) Parse(string speech)
    {
        var text = speech.Trim().TrimEnd('.', '!', '?'); text = Regex.Replace(text, @"^(?:hey )?jarvis[, ]+|^please\s+", "", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var media = Regex.Match(text, @"^(play|pause|next)(?: (?:the )?(?:music|media|track|song))?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (media.Success) return (WindowsCommandProtocol.Media, JsonSerializer.Serialize(new { action = media.Groups[1].Value.ToLowerInvariant() }));
        if (Regex.IsMatch(text, @"^lock (?:my |the )?(?:laptop|computer|workstation)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return (WindowsCommandProtocol.Lock, "{}");
        var volume = Regex.Match(text, @"^(?:turn (?:the )?)?volume (up|down)$|^(mute) (?:the )?(?:volume|laptop|computer)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (volume.Success) return (WindowsCommandProtocol.Volume, JsonSerializer.Serialize(new { action = volume.Groups[1].Success ? volume.Groups[1].Value.ToLowerInvariant() : "mute", steps = 1 }));
        var notification = Regex.Match(text, @"^show (?:a )?notification (.{1,500})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (notification.Success) return (WindowsCommandProtocol.Notification, JsonSerializer.Serialize(new { message = notification.Groups[1].Value }));
        var matches = new List<(string, string)>();
        foreach (var (section, capability, field) in new[] { ("AppAliases", WindowsCommandProtocol.App, "appId"), ("UrlAliases", WindowsCommandProtocol.Url, "urlId"), ("FolderAliases", WindowsCommandProtocol.Files, "folderId") }) {
            foreach (var alias in configuration.GetSection("WindowsActions:" + section).GetChildren()) {
                if (!WindowsCommandProtocol.IsId(alias.Value)) continue;
                if (string.Equals(text, "open " + alias.Key, StringComparison.OrdinalIgnoreCase)) {
                    var value = new Dictionary<string, object> { [field] = alias.Value! }; if (capability == WindowsCommandProtocol.Files) value["name"] = "";
                    matches.Add((capability, JsonSerializer.Serialize(value)));
                }
                if (capability == WindowsCommandProtocol.Files) {
                    var find = Regex.Match(text, @"^find files named (.{1,100}) in " + Regex.Escape(alias.Key) + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                    if (find.Success) matches.Add((capability, JsonSerializer.Serialize(new { folderId = alias.Value, name = find.Groups[1].Value })));
                }
            }
        }
        Guard.Require(matches.Count == 1, 400, "Use one explicit laptop action and an owner-configured app, URL or folder alias.");
        _ = WindowsCommandProtocol.Parse(matches[0].Item1, matches[0].Item2);
        return matches[0];
    }
}
