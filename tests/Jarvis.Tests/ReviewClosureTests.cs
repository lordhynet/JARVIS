using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jarvis.Api.Integrations;
using Jarvis.Api.Security;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed class ReviewClosureTests
{
    [Fact]
    public async Task Untrusted_invocation_origin_cannot_be_lowered_by_a_request_flag()
    {
        using var host = new JarvisApiFactory(); var services = host.Services; var sessions = services.GetRequiredService<ISessionStore>();
        var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        await sessions.AddAsync(new(id, ActivityType.General, "Fixture", SessionStatus.Active, null, null, now, now, []));
        var tools = services.GetRequiredService<ToolService>();
        foreach (var origin in new[] { InvocationOrigin.Agent, InvocationOrigin.Voice, InvocationOrigin.ExternalContent }) {
            using var scope = CoreInvocationContext.Enter(origin);
            var action = await tools.RequestAsync(new(Guid.NewGuid(), id, "light.desk", "turn_on", false), default);
            Assert.Equal(ToolInvocationStatus.Rejected, action.Invocation.Status);
            await Assert.ThrowsAsync<DomainException>(() => tools.ConfirmAsync(action.Invocation.Id, true, default));
        }
    }
    [Fact]
    public void Cache_maintenance_preserves_canonical_and_financial_data()
    {
        var state = new RuntimeState(); var now = DateTimeOffset.UtcNow; var id = Guid.NewGuid();
        state.ProviderResults[id] = new(id, id, "fixture", "{}", now.AddDays(-8));
        state.UsageReservations[id] = new(id, id, "fixture", now, 1);
        Assert.Equal(1, RuntimeMaintenance.Prune(state, now)); Assert.Single(state.UsageReservations);
        Assert.Empty(state.ProviderResults);
    }
    [Fact]
    public void Realtime_audio_usage_uses_audio_rates_and_missing_breakdowns_use_the_higher_bound()
    {
        var pricing = new RealtimePricing(32, 4, 2, 1, 64, 8, .01m, "fixture");
        using var audio = JsonDocument.Parse("""{"input_tokens":1000,"output_tokens":100,"input_token_details":{"audio_tokens":1000,"text_tokens":0},"output_token_details":{"audio_tokens":100,"text_tokens":0}}""");
        using var text = JsonDocument.Parse("""{"input_tokens":1000,"output_tokens":100,"input_token_details":{"audio_tokens":0,"text_tokens":1000},"output_token_details":{"audio_tokens":0,"text_tokens":100}}""");
        using var unknown = JsonDocument.Parse("""{"input_tokens":1000,"output_tokens":100}""");
        Assert.Equal(.0384m, pricing.Cost(audio.RootElement)); Assert.Equal(.0048m, pricing.Cost(text.RootElement));
        Assert.Equal(pricing.Cost(audio.RootElement), pricing.Cost(unknown.RootElement));
        Assert.Equal(.02m, pricing.Transcription(60));
        using var cached = JsonDocument.Parse("""{"input_tokens":1000,"output_tokens":0,"input_token_details":{"audio_tokens":1000,"text_tokens":0,"cached_tokens":500,"cached_tokens_details":{"audio_tokens":500,"text_tokens":0}}}""");
        Assert.Equal(.017m, pricing.Cost(cached.RootElement));
    }
    [Fact]
    public void Missing_audio_rates_cannot_enable_realtime_dispatch()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["AI:Voice:TextInputUsdPerMillion"] = "1" }).Build();
        Assert.Equal(503, Assert.Throws<DomainException>(() => RealtimePricing.Read(config)).StatusCode);
    }
    [Fact]
    public async Task Raw_core_key_is_rejected_in_hub_URL_and_short_lived_ticket_connects_once()
    {
        using var factory = new JarvisApiFactory(); using var client = factory.CreateAuthorisedClient();
        using var anonymous = factory.CreateClient(); using var denied = await anonymous.PostAsync("/hubs/status/negotiate?negotiateVersion=1&access_token=" + JarvisApiFactory.TestKey, null);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var issued = await client.PostAsJsonAsync("/api/hubs/ticket", new { }); issued.EnsureSuccessStatusCode();
        var ticket = (await issued.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("ticket").GetString()!;
        HubConnection Connection() => new HubConnectionBuilder().WithUrl("http://localhost/hubs/status?access_token=" + ticket, options => {
            options.Transports = HttpTransportType.WebSockets; options.SkipNegotiation = true;
            options.WebSocketFactory = async (context, ct) => await factory.Server.CreateWebSocketClient().ConnectAsync(context.Uri, ct);
        }).Build();
        await using var connection = Connection(); await connection.StartAsync(); Assert.Equal("pong", await connection.InvokeAsync<string>("Ping"));
        await using var replay = Connection(); await Assert.ThrowsAnyAsync<Exception>(() => replay.StartAsync());
    }
    [Fact]
    public async Task Hub_tickets_expire_and_key_rotation_invalidates_pending_tickets()
    {
        var clock = new BudgetClock(); using var original = new JarvisApiFactory();
        using var host = original.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<TimeProvider>(clock)));
        var service = host.Services.GetRequiredService<HubTickets>();
        string Value(object result) => JsonSerializer.SerializeToElement(result).GetProperty("ticket").GetString()!;
        var expired = Value(await service.IssueAsync(default)); clock.Now = clock.Now.AddSeconds(31);
        await Assert.ThrowsAsync<DomainException>(() => service.ConsumeAsync(expired, default));
        var rotated = Value(await service.IssueAsync(default)); host.Services.GetRequiredService<IConfiguration>()["Jarvis:ApiKey"] = "rotated-test-root-key";
        await Assert.ThrowsAsync<DomainException>(() => service.ConsumeAsync(rotated, default));
    }
    [Fact]
    public async Task Global_audio_lease_excludes_calibration_local_recording_and_realtime_across_sessions()
    {
        using var host = new JarvisApiFactory(); var services = host.Services; var devices = services.GetRequiredService<DeviceService>();
        var sessions = services.GetRequiredService<ISessionStore>(); var audio = services.GetRequiredService<AudioOwnership>();
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() }; var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 2; i++) {
            var id = "device" + i; await devices.RegisterAsync(new(id, "Fixture", DeviceKind.WindowsAgent, new(true, true, true, false, [])));
            await devices.VerifyAsync(id); await sessions.AddAsync(new(ids[i], ActivityType.General, "Fixture", SessionStatus.Active, id, null, now, now, []));
        }
        var local = await audio.StartLocalAsync(ids[0], "device0", default);
        await Assert.ThrowsAsync<DomainException>(() => audio.StartLocalAsync(ids[1], "device1", default));
        await Assert.ThrowsAsync<DomainException>(() => audio.StartAsync(new(ids[1], "device1", true), default));
        await Assert.ThrowsAsync<DomainException>(() => audio.StartCalibrationAsync("device1", default));
        await audio.ReleaseAsync(local, default); var ticket = await audio.StartAsync(new(ids[1], "device1", true), default);
        await Assert.ThrowsAsync<DomainException>(() => audio.StartLocalAsync(ids[0], "device0", default));
        await audio.ReleaseAsync(ticket.Lease, default);
    }
}
