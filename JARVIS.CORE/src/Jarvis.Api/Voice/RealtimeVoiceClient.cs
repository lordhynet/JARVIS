using System.Net.WebSockets;
using System.Text.Json;
using Jarvis.Contracts;

namespace Jarvis.Api.Voice;

internal sealed record VoiceStreamMode(VoiceMode Mode, decimal? ConfirmedGbpForTenMinutes = null, bool FollowActivity = false);

// One receive survives the cutover. Cancelling a window's wait must not abort the client's socket.
internal sealed class RealtimeVoiceClient(WebSocket socket, CancellationToken disconnected)
{
    private Task<JsonElement>? pending;
    private int stopRequested;
    private int lockInterrupted;
    private VoiceStreamMode? modeSelection;

    public WebSocketState State => socket.State;
    public bool LocalGreeting { get; set; }
    // Stopwatch timestamp of ticket authentication. Window 1 reports ready latency from it; the first reply audio clears it.
    private long authenticatedAt;
    public void RecordAuthenticated(long timestamp) => Interlocked.Exchange(ref authenticatedAt, timestamp);
    public long AuthenticatedAt => Interlocked.Read(ref authenticatedAt);
    public long TakeAuthenticatedAt() => Interlocked.Exchange(ref authenticatedAt, 0);
    public bool StopRequested => Volatile.Read(ref stopRequested) != 0;
    public void RecordStop() => Volatile.Write(ref stopRequested, 1);
    public bool LockInterrupted => Volatile.Read(ref lockInterrupted) != 0;
    public void RecordLockInterrupt() => Volatile.Write(ref lockInterrupted, 1);
    public VoiceStreamMode? ModeSelection
    {
        get => Volatile.Read(ref modeSelection);
        set => Volatile.Write(ref modeSelection, value);
    }

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
