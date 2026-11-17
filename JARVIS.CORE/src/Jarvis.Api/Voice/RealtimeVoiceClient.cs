using System.Net.WebSockets;
using System.Text.Json;

namespace Jarvis.Api.Voice;

// One receive survives the cutover. Cancelling a window's wait must not abort the client's socket.
internal sealed class RealtimeVoiceClient(WebSocket socket, CancellationToken disconnected)
{
    private Task<JsonElement>? pending;
    private int stopRequested;

    public WebSocketState State => socket.State;
    public bool StopRequested => Volatile.Read(ref stopRequested) != 0;
    public void RecordStop() => Volatile.Write(ref stopRequested, 1);

    public async Task<JsonElement> ReadAsync(CancellationToken ct)
    {
        pending ??= RealtimeGateway.ReadAsync(socket, 70000, disconnected);
        var message = await pending.WaitAsync(ct);
        pending = null;
        return message;
    }

    public Task SendAsync(object value, CancellationToken ct) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value), WebSocketMessageType.Text, true, ct);

    public void ObserveClosedReceive()
    {
        if (pending is not null)
            _ = pending.ContinueWith(task => { _ = task.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
}
