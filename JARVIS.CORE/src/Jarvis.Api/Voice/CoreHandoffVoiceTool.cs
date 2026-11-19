using System.Text.RegularExpressions;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;
namespace Jarvis.Api.Voice;
public sealed class CoreHandoffVoiceTool(IRuntimeReader runtime, CompanionHandoffService handoffs, TimeProvider clock)
{
    public const string Name = "core_handoff";
    public async Task<object> RunAsync(Guid session, Guid source, Func<CancellationToken, Task<bool>>? currentAudio, CancellationToken ct)
    {
        Guard.Require(CoreInvocationContext.Origin == InvocationOrigin.Voice && currentAudio is not null && await currentAudio(ct), 403, "A current live voice window is required.");
        var request = await runtime.ReadAsync(s => {
            Guard.Require(s.AudioLease is { } lease && lease.SessionId == session && !lease.Locked && lease.ExpiresAt > clock.GetUtcNow(), 403, "Unlock the source device first.");
            Guard.Require(s.VoiceTurns.TryGetValue(source, out var turn) && turn.SessionId == session && turn.Role == ChatRoles.User && !turn.Restricted &&
                turn.At >= clock.GetUtcNow().AddSeconds(-90) && !s.RetiredSessions.Contains(session), 409, "Use fresh captured speech from this conversation.");
            var match = Regex.Match(turn.Text.Trim(), @"^(?:please )?(?:continue|carry on|move)(?: (?:this|the) conversation)? on (?:my |the )?(phone|laptop|browser)(?: please)?[.!]?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            Guard.Require(match.Success, 400, "Say continue on my phone, laptop or browser. Choose an exact device in the dashboard if ambiguous.");
            return (Device: s.AudioLease!.DeviceId, Kind: match.Groups[1].Value.ToLowerInvariant());
        }, ct);
        var view = await handoffs.ControlAsync(new(request.Device, Locked: false), ct);
        var targets = view.Targets.Where(t => request.Kind == "browser" ? t.Browser : !t.Browser && t.Kind ==
            (request.Kind == "phone" ? DeviceKind.AndroidCompanion : DeviceKind.WindowsAgent)).ToArray();
        Guard.Require(targets.Length == 1, 409, "Choose one online target in the device controls; no device was selected automatically.");
        await handoffs.ControlAsync(new(request.Device, "start", session, TargetDeviceId: targets[0].DeviceId,
            RequestId: AgentRuntime.StableId($"voice-handoff-v1:{session}:{source}"), Locked: false), ct);
        return new { status = "waiting_for_acceptance", instruction = "The other device must accept. Audio has not transferred yet." };
    }
}
