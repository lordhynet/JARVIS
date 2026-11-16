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
        using var client = await context.WebSockets.AcceptWebSocketAsync();
        var session = new RealtimeVoiceSession(client, ownership, connections, ai, budget, runtime, sessions, clock,
            tools, transcripts, fenceChanges);
        await session.RunAsync(context.RequestAborted);
    }

    internal static async Task<AudioLease> AuthenticateAsync(WebSocket client, AudioOwnership ownership, CancellationToken ct)
    {
        var first = await ReadAsync(client, 2048, ct);
        Guard.Require(first.GetProperty("type").GetString() == "authenticate", 401, "A one-time voice ticket is required.");
        return await ownership.ConsumeAsync(Guard.Text(first.GetProperty("ticket").GetString(), "Ticket", 100), ct);
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
