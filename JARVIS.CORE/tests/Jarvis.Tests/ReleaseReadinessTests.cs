using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Jarvis.Api;
using Jarvis.Api.Security;
using Jarvis.Api.Voice;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Intelligence;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jarvis.Tests;

public sealed partial class RealtimeProtocolTests
{
    [Fact]
    public async Task An_anonymous_proxy_flood_cannot_use_a_paired_devices_voice_allowance()
    {
        using var root = new ProviderFactory(_ => throw new InvalidOperationException("Provider HTTP calls are unexpected."));
        var provider = new ProviderSocket();
        using var host = root.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            services.AddSingleton<IRealtimeConnectionFactory>(provider)));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var ct = deadline.Token;
        var devices = host.Services.GetRequiredService<DeviceService>();
        const string phone = "tailscale-voice-fixture";
        await devices.RegisterAsync(new(phone, "Phone", DeviceKind.AndroidCompanion, new(true, true, false, false, [])));
        await devices.VerifyAsync(phone);
        var credential = await host.Services.GetRequiredService<DeviceCredentials>().IssueAsync(phone, ct);
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await host.Services.GetRequiredService<ISessionStore>().AddAsync(new(id, ActivityType.General, "Proxy fixture",
            SessionStatus.Active, phone, null, now, now, []), ct);

        using var client = host.CreateClient();
        for (var i = 0; i < CoreRequestLimits.AnonymousPermits + 2; i++)
        {
            using var anonymous = new HttpRequestMessage(HttpMethod.Post, CoreRoutes.VoiceTickets);
            anonymous.Headers.TryAddWithoutValidation("X-Forwarded-For", $"198.51.100.{i + 1}");
            using var result = await client.SendAsync(anonymous, ct);
            Assert.Equal(i < CoreRequestLimits.AnonymousPermits ? HttpStatusCode.Unauthorized : HttpStatusCode.TooManyRequests, result.StatusCode);
        }
        using var exhausted = await client.GetAsync(CoreRoutes.VoiceStream, ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, exhausted.StatusCode);

        using var request = new HttpRequestMessage(HttpMethod.Post, CoreRoutes.VoiceTickets)
            { Content = JsonContent.Create(new VoiceStartRequest(id, phone, true)) };
        request.Headers.Add(CoreHeaders.ApiKey, credential.Secret);
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "198.51.100.1");
        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var ticket = (await response.Content.ReadFromJsonAsync<VoiceTicket>(ct))!;
        using var socket = await host.Server.CreateWebSocketClient().ConnectAsync(new("ws://localhost/voice-stream"), ct);
        await Send(socket, new { type = "authenticate", ticket = ticket.Ticket }, ct);
        Assert.Equal("ready", (await RealtimeGateway.ReadAsync(socket, 4096, ct)).GetProperty("type").GetString());
        await Send(socket, new { type = "stop" }, ct);
        Assert.Equal("ended", (await RealtimeGateway.ReadAsync(socket, 4096, ct)).GetProperty("type").GetString());
    }
}
