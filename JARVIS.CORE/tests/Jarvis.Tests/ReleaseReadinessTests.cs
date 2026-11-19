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
        await ReadFrameAsync(socket, "ready", ct);
        await Send(socket, new { type = "stop" }, ct);
        await ReadFrameAsync(socket, "ended", ct);
    }
}

public sealed class ReleaseReadinessTests
{
    [Theory]
    [InlineData("Chat", TextCapability.Conversation, 1500)]
    [InlineData("Coach", TextCapability.JsonReasoning, 2048)]
    public async Task Luna_uses_the_reviewed_profile_and_responses_shape(string profile, TextCapability capability, int outputLimit)
    {
        const string model = "gpt-6-luna";
        var calls = 0;
        using var root = new ProviderFactory(async request =>
        {
            calls++;
            Assert.Equal("https://api.openai.com/v1/responses", request.RequestUri!.AbsoluteUri);
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(model, body.GetProperty("model").GetString());
            Assert.False(body.GetProperty("store").GetBoolean());
            Assert.Equal(outputLimit, body.GetProperty("max_output_tokens").GetInt32());
            Assert.Equal("Return a short JSON response.", body.GetProperty("instructions").GetString());
            Assert.Equal("Fixture input", body.GetProperty("input").GetString());
            Assert.False(body.TryGetProperty("tools", out _));
            if (capability == TextCapability.JsonReasoning)
                Assert.Equal("json_object", body.GetProperty("text").GetProperty("format").GetProperty("type").GetString());
            else Assert.False(body.TryGetProperty("text", out _));
            return new(HttpStatusCode.OK) { Content = new StringContent(
                """{"status":"completed","output":[{"type":"message","content":[{"type":"output_text","text":"Fixture reply"}]}],"usage":{"input_tokens":3,"output_tokens":2}}""",
                Encoding.UTF8, "application/json") };
        });
        using var host = root.WithWebHostBuilder(builder =>
        {
            builder.UseSetting($"AI:{profile}:Model", model);
            builder.UseSetting($"AI:{profile}:RateCardModel", model);
            if (profile == "Chat") builder.UseSetting("OpenAI:Model", model);
        });
        var service = host.Services.GetRequiredService<ITextReasoningProvider>();
        var result = await service.ReasonAsync(new(Guid.NewGuid(), Guid.NewGuid(), profile, capability,
            ReasoningDataClass.Personal, true, "Return a short JSON response.", [new(ChatRoles.User, "Fixture input")], UsageLane.Interactive), default);
        Assert.Equal("Fixture reply", result.Text);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Core_uses_process_only_framework_data_protection()
    {
        using var host = new JarvisApiFactory();
        Assert.IsType<EphemeralDataProtectionProvider>(host.Services.GetRequiredService<IDataProtectionProvider>());
        Assert.DoesNotContain(host.Services.GetServices<IHostedService>(),
            service => service.GetType().Name == "DataProtectionHostedService");
    }
}
