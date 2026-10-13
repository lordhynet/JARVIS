using Jarvis.Contracts;
using Jarvis.Orchestrator.Sessions;
using Microsoft.AspNetCore.SignalR;

namespace Jarvis.Api.Hubs;

/// <summary>
/// Live status channel for devices (Windows agent, voice device, S24, Quest).
/// Clients listen for <see cref="HubMethods.SessionEvent"/>.
/// </summary>
public sealed class StatusHub(ILogger<StatusHub> logger) : Hub
{
    public override Task OnConnectedAsync()
    {
        logger.LogInformation("Device connected to status hub: {ConnectionId}", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    /// <summary>Simple round-trip check a device can call after connecting.</summary>
    public string Ping() => "pong";
}

/// <summary>Bridges the orchestrator's notifier interface to SignalR.</summary>
public sealed class SignalRSessionNotifier(IHubContext<StatusHub> hub) : ISessionNotifier
{
    public Task PublishAsync(SessionStatusEvent evt, CancellationToken ct = default) =>
        hub.Clients.All.SendAsync(HubMethods.SessionEvent, evt, ct);
}
