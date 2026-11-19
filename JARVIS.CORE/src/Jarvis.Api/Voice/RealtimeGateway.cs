using System.Net.WebSockets;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Api.Integrations;
using Jarvis.Api.Security;
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
    VoiceTranscriptService transcripts, AudioFenceChanges fenceChanges, OpenAiProvider transcription, CoreRequestLimits limits)
{
    public async Task RunAsync(HttpContext context)
    {
        using var invocation = CoreInvocationContext.Enter(InvocationOrigin.Voice);
        var privacyStatus = await privacy.StatusAsync(context.RequestAborted);
        PrivacyContext.RequireWrite(privacyStatus);
        using var privacyScope = PrivacyContext.Enter(privacyStatus.Epoch);
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = limits.AdmitAnonymous() ? StatusCodes.Status400BadRequest : StatusCodes.Status429TooManyRequests;
            return;
        }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var client = new RealtimeVoiceClient(socket, context.RequestAborted);
        AudioLease? lease = null;
        try
        {
            using var authentication = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
            authentication.CancelAfter(TimeSpan.FromSeconds(8));
            var (ticket, continuous, startEveryday, negotiateMode, localGreeting) = await AuthenticateAsync(socket, ownership, authentication.Token);
            lease = ticket.Lease;
            client.RecordAuthenticated(clock.GetTimestamp());
            Guard.Require(limits.AdmitVoiceStream(lease.DeviceId), 429, "Too many voice connections for this device.");
            if (startEveryday) client.ModeSelection = new(VoiceMode.Everyday);
            client.LocalGreeting = localGreeting;
            var window = new VoiceWindowLimits(ticket.WindowSeconds);
            var negotiation = new RealtimeVoiceNegotiation(client, lease, ownership, runtime, transcription, transcripts, clock);
            RealtimeVoiceSession CreateWindow(int number) => new(client, ticket.Lease, window, number, continuous, negotiateMode,
                ownership, connections, ai, budget, runtime, sessions, clock, tools, transcripts, fenceChanges);
            RealtimeVoiceSession? current = CreateWindow(1);
            string? ending = null;
            var lastQuoteWindow = 0;
            var fallbackTried = false;
            while (current is not null)
            {
                var number = current.Number;
                try
                {
                    var result = await current.RunAsync(CreateWindow, context.RequestAborted);
                    current = result.Next;
                    ending = result.Ending;
                }
                catch (SeriousVoiceConfirmationRequired offer) when (lastQuoteWindow != number)
                {
                    lastQuoteWindow = number;
                    if (client.StopRequested || !await negotiation.ConfirmPriceAsync(number, offer.Estimate, context.RequestAborted))
                    {
                        ending = "stopped";
                        break;
                    }
                    current = CreateWindow(number);
                }
                catch (SeriousVoiceUnavailableException error) when (negotiateMode && !fallbackTried)
                {
                    fallbackTried = true;
                    if (client.StopRequested || !await negotiation.ConfirmEverydayAsync(error.Message, context.RequestAborted))
                    {
                        ending = client.StopRequested ? "stopped" : (error.InnerException as DomainException)?.Code ?? "voice_mode_declined";
                        break;
                    }
                    current = CreateWindow(number);
                }
            }

            using var last = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await ownership.ReleaseAsync(lease, last.Token);
            lease = null;
            CoreTelemetry.RecordVoiceEnded(ending);
            await client.SendAsync(new { type = "ended", reason = ending }, last.Token);
        }
        catch (Exception error) when (error is DomainException or SeriousVoiceUnavailableException or SeriousVoiceConfirmationRequired or JsonException or WebSocketException or OperationCanceledException or FormatException or KeyNotFoundException or HttpRequestException)
        {
            using var last = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                var detail = error switch
                {
                    DomainException or SeriousVoiceUnavailableException => error.Message,
                    SeriousVoiceConfirmationRequired => "The Serious voice estimate changed. Start again to review it.",
                    _ => "Voice stopped. Reconnect to resume the saved session."
                };
                var code = client.LockInterrupted ? VoiceEndReasons.DeviceLocked :
                    (error as DomainException ?? error.InnerException as DomainException)?.Code ??
                    (error is SeriousVoiceUnavailableException ? "serious_voice_unavailable" : null);
                if (lease is null && !limits.AdmitAnonymous())
                {
                    detail = "Too many unauthenticated requests.";
                    code = "rate_limited";
                }
                CoreTelemetry.RecordVoiceEnded(code ?? "error");
                await client.SendAsync(new { type = "error", detail, code }, last.Token);
                if (code is VoiceEndReasons.DeviceLocked or VoiceEndReasons.SeriousDailyCap or VoiceEndReasons.GbpBudgetCeiling)
                    await client.SendAsync(new { type = "ended", reason = code }, last.Token);
            }
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

    private static async Task<(VoiceTicketRecord Ticket, bool Continuous, bool StartEveryday, bool NegotiateMode, bool LocalGreeting)> AuthenticateAsync(
        WebSocket client, AudioOwnership ownership, CancellationToken ct)
    {
        var first = await ReadAsync(client, 2048, ct);
        Guard.Require(first.ValueKind == JsonValueKind.Object && first.TryGetProperty("type", out var type) &&
            type.ValueKind == JsonValueKind.String && type.ValueEquals("authenticate"), 401, "A one-time voice ticket is required.");
        if (!first.TryGetProperty("ticket", out var token) || token.ValueKind != JsonValueKind.String)
            throw new DomainException(401, "A one-time voice ticket is required.");
        var ticket = await ownership.ConsumeTicketAsync(Guard.Text(token.GetString(), "Ticket", 100), ct);
        var continuous = first.TryGetProperty("continuous", out var renewal) && renewal.ValueKind == JsonValueKind.True;
        return (ticket, continuous,
            first.TryGetProperty("startEveryday", out var everyday) && everyday.ValueKind == JsonValueKind.True,
            continuous && first.TryGetProperty("voiceModeNegotiation", out var negotiation) && negotiation.ValueKind == JsonValueKind.True,
            first.TryGetProperty("localGreeting", out var greeting) && greeting.ValueKind == JsonValueKind.True);
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
