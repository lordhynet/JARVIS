using System.Net.WebSockets;
using System.Text.Json;

namespace Jarvis.Windows.Client;

public interface IVoiceSocket : IAsyncDisposable
{
    Task ConnectAsync(Connection connection, CancellationToken ct);
    Task SendAsync(object frame, CancellationToken ct);
    Task<JsonElement> ReceiveAsync(CancellationToken ct);
}

public sealed class VoiceSocket : IVoiceSocket
{
    private readonly ClientWebSocket socket = new();
    private readonly SemaphoreSlim sends = new(1);
    public Task ConnectAsync(Connection connection, CancellationToken ct)
    {
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
        socket.Options.RemoteCertificateValidationCallback = (_, certificate, _, errors) => connection.AcceptCertificate(certificate, errors);
        return socket.ConnectAsync(new UriBuilder(connection.Origin) { Scheme = "wss", Path = Jarvis.Contracts.CompanionRoutes.VoiceStream }.Uri, ct);
    }
    public async Task SendAsync(object frame, CancellationToken ct)
    {
        await sends.WaitAsync(ct);
        try { await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(frame), WebSocketMessageType.Text, true, ct); }
        finally { sends.Release(); }
    }
    public async Task<JsonElement> ReceiveAsync(CancellationToken ct)
    {
        using var bytes = new MemoryStream(); var buffer = new byte[8192];
        WebSocketReceiveResult message;
        do {
            message = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (message.MessageType != WebSocketMessageType.Text || bytes.Length + message.Count > 1_400_000)
                throw new IOException("Core voice closed or returned an invalid frame.");
            bytes.Write(buffer, 0, message.Count);
        } while (!message.EndOfMessage);
        return JsonSerializer.Deserialize<JsonElement>(bytes.GetBuffer().AsSpan(0, (int)bytes.Length));
    }
    public ValueTask DisposeAsync() { socket.Abort(); socket.Dispose(); sends.Dispose(); return ValueTask.CompletedTask; }
}
