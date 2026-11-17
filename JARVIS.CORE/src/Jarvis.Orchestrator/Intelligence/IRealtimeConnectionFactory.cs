using System.Net.WebSockets;

namespace Jarvis.Orchestrator.Intelligence;

/// <summary>Transport port for opening the configured realtime provider connection.</summary>
public interface IRealtimeConnectionFactory
{
    Task<WebSocket> ConnectAsync(string model, CancellationToken ct);
}
