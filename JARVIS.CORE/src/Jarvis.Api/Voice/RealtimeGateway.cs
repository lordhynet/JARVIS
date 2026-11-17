using System.Net.WebSockets;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Api.Integrations;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Jarvis.Orchestrator.Workflows;
using Jarvis.Orchestrator.Intelligence;

namespace Jarvis.Api.Voice;

public sealed class RealtimeConnectionFactory(IConfiguration config) : IRealtimeConnectionFactory
{
    public async Task<WebSocket> ConnectAsync(string model, CancellationToken ct)
    {
        var socket = new ClientWebSocket(); socket.Options.SetRequestHeader("Authorization", "Bearer " + config[ConfigKeys.OpenAiApiKey]);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
        try { await socket.ConnectAsync(new Uri("wss://api.openai.com/v1/realtime?model=" + Uri.EscapeDataString(model)), ct); return socket; }
        catch { socket.Dispose(); throw; }
    }
}
public sealed class RealtimeGateway(AudioOwnership ownership, IRealtimeConnectionFactory connections, ManagedAi ai,
    UsageBudget budget, IRuntimeStore runtime, ISessionStore sessions, IPrivacyReader privacy, TimeProvider clock, CoreVoiceTools tools,
    VoiceTranscriptService transcripts, AudioFenceChanges fenceChanges)
{
    public async Task RunAsync(HttpContext context)
    {
        using var invocation = CoreInvocationContext.Enter(InvocationOrigin.Voice);
        var privacyStatus = await privacy.StatusAsync(context.RequestAborted);
        PrivacyContext.RequireWrite(privacyStatus);
        using var privacyScope = PrivacyContext.Enter(privacyStatus.Epoch);
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var client = new RealtimeVoiceClient(socket, context.RequestAborted);
        AudioLease? lease = null;
        try
        {
            using var authentication = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            authentication.CancelAfter(TimeSpan.FromSeconds(8));
            var (ticket, continuous) = await AuthenticateAsync(socket, ownership, authentication.Token);
            lease = ticket.Lease;
            var window = new VoiceWindowLimits(ticket.WindowSeconds);
            RealtimeVoiceSession CreateWindow(int number) => new(client, ticket.Lease, window, number, continuous,
                ownership, connections, ai, budget, runtime, sessions, clock, tools, transcripts, fenceChanges);
            RealtimeVoiceSession? current = CreateWindow(1);
            string? ending = null;
            while (current is not null)
            {
                var result = await current.RunAsync(CreateWindow, context.RequestAborted);
                current = result.Next;
                ending = result.Ending;
            }

            using var last = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await ownership.ReleaseAsync(lease, last.Token);
            lease = null;
            await client.SendAsync(new { type = "ended", reason = ending }, last.Token);
        }
        catch (Exception error) when (error is DomainException or JsonException or WebSocketException or OperationCanceledException or FormatException or KeyNotFoundException)
        {
            using var last = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await client.SendAsync(new { type = "error", detail = error is DomainException d ? d.Message : "Voice stopped. Reconnect to resume the saved session." }, last.Token); }
            catch { /* The client can already be disconnected. */ }
        }
        finally
        {
            using var close = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            if (lease is not null)
                try { await ownership.ReleaseAsync(lease, close.Token); }
                catch (Exception error) when (error is DomainException or OperationCanceledException) { /* Expiry remains authoritative. */ }
            if (socket.State == WebSocketState.Open)
                try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Voice stopped", close.Token); }
                catch { /* Disposal closes any remaining receive. */ }
            client.ObserveClosedReceive();
        }
    }

    private static async Task<(VoiceTicketRecord Ticket, bool Continuous)> AuthenticateAsync(WebSocket client, AudioOwnership ownership, CancellationToken ct)
    {
        var first = await ReadAsync(client, 2048, ct);
        Guard.Require(first.GetProperty("type").GetString() == "authenticate", 401, "A one-time voice ticket is required.");
        var ticket = await ownership.ConsumeTicketAsync(Guard.Text(first.GetProperty("ticket").GetString(), "Ticket", 100), ct);
        return (ticket, first.TryGetProperty("continuous", out var continuous) && continuous.ValueKind == JsonValueKind.True);
    }

    public static async Task<JsonElement> ReadAsync(WebSocket socket, int maximum, CancellationToken ct)
    {
        using var data = new MemoryStream(); var buffer = new byte[8192]; WebSocketReceiveResult message;
        do
        {
            message = await socket.ReceiveAsync(buffer, ct);
            Guard.Require(message.MessageType == WebSocketMessageType.Text, 400, "Voice protocol expects JSON text frames.");
            Guard.Require(data.Length + message.Count <= maximum, 413, "Voice frame too large."); data.Write(buffer, 0, message.Count);
        } while (!message.EndOfMessage);
        return JsonSerializer.Deserialize<JsonElement>(data.ToArray());
    }
}
