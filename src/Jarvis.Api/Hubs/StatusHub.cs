using Jarvis.Contracts;
using Jarvis.Orchestrator.Sessions;
using Microsoft.AspNetCore.SignalR;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Api.Security;

namespace Jarvis.Api.Hubs;

/// <summary>
/// Live status channel for devices (Windows agent, voice device, S24, Quest).
/// Clients listen for <see cref="HubMethods.SessionEvent"/>.
/// </summary>
public sealed class StatusHub(ILogger<StatusHub> logger, IPrivacyReader privacy) : Hub
{
    public override async Task OnConnectedAsync()
    {
        var status = await privacy.StatusAsync(Context.ConnectionAborted);
        if (status.Erased) { Context.Abort(); return; }
        var fingerprint = Context.GetHttpContext()?.Items[ApiKeyMiddleware.FingerprintItem] as string;
        if (fingerprint is null) { Context.Abort(); return; }
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(status.Epoch, fingerprint), Context.ConnectionAborted);
        logger.LogInformation("Device connected to status hub: {ConnectionId}", Context.ConnectionId);
        await base.OnConnectedAsync();
    }
    internal static string Group(long epoch, string fingerprint) => $"core:{epoch}:{fingerprint}";

    /// <summary>Simple round-trip check a device can call after connecting.</summary>
    public string Ping() => "pong";
}

/// <summary>Bridges the orchestrator's notifier interface to SignalR.</summary>
public sealed class SignalRSessionNotifier(IHubContext<StatusHub> hub, IPrivacyReader privacy, IConfiguration config) : ISessionNotifier
{
    public async Task PublishAsync(SessionStatusEvent evt, CancellationToken ct = default)
    {
        var status = await privacy.StatusAsync(ct);
        if (status.Erased || PrivacyContext.Epoch is long epoch && epoch != status.Epoch) return;
        var key = config[ConfigKeys.CoreApiKey]; if (string.IsNullOrWhiteSpace(key)) return;
        await hub.Clients.Group(StatusHub.Group(status.Epoch, PrivacyData.Hash(key))).SendAsync(HubMethods.SessionEvent, evt, ct);
    }
}
