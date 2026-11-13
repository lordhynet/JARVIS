using System.Text.Json;
using System.Text.RegularExpressions;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Sessions;

namespace Jarvis.Orchestrator.Runtime;

public interface IHomeControl
{
    Task<string> ExecuteAsync(string entityId, string action, CancellationToken ct);
}

public sealed class ToolService(IRuntimeStore store, ISessionReader sessions, IHomeControl home, TimeProvider clock)
{
    public Task<int> RecoverInterruptedAsync(CancellationToken ct) => store.UpdateAsync(s =>
    {
        var stale = s.Tools.Values.Where(t => t.Invocation.Status == ToolInvocationStatus.Running &&
            clock.GetUtcNow() - t.Invocation.RequestedAt > TimeSpan.FromMinutes(7)).ToList();
        foreach (var tool in stale)
            s.Tools[tool.Invocation.Id] = tool with
            {
                Invocation = tool.Invocation with { Status = ToolInvocationStatus.Uncertain },
                Result = "Execution was interrupted. Check the physical device before requesting another action.",
                CompletedAt = clock.GetUtcNow()
            };
        return stale.Count;
    }, ct);

    public async Task<AuditedTool> RequestAsync(HomeActionRequest request, CancellationToken ct)
    {
        Guard.Require(request.RequestId != Guid.Empty, 400, "RequestId is required.");
        Guard.Require(request.EntityId is not null && Regex.IsMatch(request.EntityId, "^(light|switch)\\.[a-z0-9_]{1,100}$"),
            400, "Only light and switch entities are supported.");
        Guard.Require(request.Action is "turn_on" or "turn_off", 400, "Only turn_on and turn_off are allowed.");
        var session = await sessions.GetAsync(request.SessionId, ct);
        Guard.Require(session is not null, 404, "Session not found.");
        Guard.Require(session!.Status == SessionStatus.Active, 409, "Session must be active.");
        var args = JsonSerializer.Serialize(new { entityId = request.EntityId, action = request.Action });
        return await store.UpdateAsync(s =>
        {
            if (s.Tools.TryGetValue(request.RequestId, out var existing))
            {
                Guard.Require(existing.Invocation.SessionId == request.SessionId && existing.Invocation.ArgumentsJson == args &&
                    existing.Invocation.TriggeredByExternalContent == request.TriggeredByExternalContent,
                    409, "RequestId was used for a different action.");
                return existing;
            }
            var rejected = request.TriggeredByExternalContent;
            var tool = new AuditedTool(new(request.RequestId, request.SessionId, "home.assistant", args,
                rejected ? ToolPermission.Denied : ToolPermission.RequiresConfirmation,
                rejected ? ToolInvocationStatus.Rejected : ToolInvocationStatus.AwaitingConfirmation,
                rejected, clock.GetUtcNow()), rejected ? "External content cannot trigger home actions." : null, null);
            s.Tools.Add(request.RequestId, tool);
            return tool;
        }, ct);
    }

    public async Task<AuditedTool> ConfirmAsync(Guid id, bool approve, CancellationToken ct)
    {
        var claimed = await store.UpdateAsync(s =>
        {
            Guard.Require(s.Tools.TryGetValue(id, out var tool), 404, "Tool request not found.");
            if (tool!.Invocation.Status != ToolInvocationStatus.AwaitingConfirmation) return (Tool: tool, Run: false);
            var expired = clock.GetUtcNow() - tool.Invocation.RequestedAt > TimeSpan.FromMinutes(5);
            var status = approve && !expired ? ToolInvocationStatus.Running : ToolInvocationStatus.Rejected;
            var updated = tool with { Invocation = tool.Invocation with { Status = status },
                Result = expired ? "Confirmation expired." : approve ? null : "Rejected by user." };
            s.Tools[id] = updated;
            return (Tool: updated, Run: status == ToolInvocationStatus.Running);
        }, ct);
        if (!claimed.Run) return claimed.Tool;
        string result;
        ToolInvocationStatus outcome;
        try
        {
            var session = await sessions.GetAsync(claimed.Tool.Invocation.SessionId, ct);
            Guard.Require(session?.Status == SessionStatus.Active, 409, "Session is no longer active.");
            var args = JsonSerializer.Deserialize<JsonElement>(claimed.Tool.Invocation.ArgumentsJson);
            result = await home.ExecuteAsync(args.GetProperty("entityId").GetString()!, args.GetProperty("action").GetString()!, ct);
            outcome = ToolInvocationStatus.Succeeded;
        }
        catch (DomainException ex)
        {
            result = ex.Message;
            outcome = ex.StatusCode == 502 ? ToolInvocationStatus.Uncertain : ToolInvocationStatus.Failed;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            // A dropped response does not prove the device failed to act. Never auto-retry.
            result = "Provider response unavailable. Check the device before requesting another action.";
            outcome = ToolInvocationStatus.Uncertain;
        }
        return await store.UpdateAsync(s =>
        {
            var updated = s.Tools[id] with { Invocation = s.Tools[id].Invocation with { Status = outcome },
                Result = result, CompletedAt = clock.GetUtcNow() };
            s.Tools[id] = updated;
            return updated;
        }, CancellationToken.None);
    }
}
