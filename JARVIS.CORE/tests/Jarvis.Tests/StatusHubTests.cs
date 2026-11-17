using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Jarvis.Contracts;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed class StatusHubTests(JarvisApiFactory factory) : IClassFixture<JarvisApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private HubConnection CreateConnection(string? key) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "/hubs/status"), o =>
            {
                // In-memory test server: long polling through the test handler.
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                if (key is not null) o.AccessTokenProvider = () => Task.FromResult<string?>(key);
            })
            .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();

    [Fact]
    public async Task Connected_device_receives_session_created_event()
    {
        await using var connection = CreateConnection(JarvisApiFactory.TestKey);
        var received = new TaskCompletionSource<SessionStatusEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<SessionStatusEvent>(HubMethods.SessionEvent, evt =>
        {
            if (evt.Type == SessionEventTypes.Created) received.TrySetResult(evt);
        });
        await connection.StartAsync();
        Assert.Equal("pong", await connection.InvokeAsync<string>("Ping"));

        var response = await factory.CreateAuthorisedClient().PostAsJsonAsync("/api/sessions",
            new CreateSessionRequest(ActivityType.French, null, "voice-pe"), Json);
        var created = await response.Content.ReadFromJsonAsync<ConversationSession>(Json);

        var evt = await received.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(created!.Id, evt.SessionId);
        Assert.Equal("voice-pe", evt.ActiveDeviceId);
    }

    [Fact]
    public async Task Hub_rejects_connection_without_a_key()
    {
        await using var connection = CreateConnection(key: null);

        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());
    }
}
