using System.Net;
using System.Net.Http.Json;
using Jarvis.Api;
using Jarvis.Api.Integrations;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;

namespace Jarvis.Tests;

public sealed class CompanionProtocolTests
{
    [Fact]
    public void Windows_contract_routes_and_headers_are_the_scoped_Core_protocol()
    {
        Assert.Equal(CoreHeaders.ApiKey, CompanionHeaders.ApiKey); Assert.Equal(CoreHeaders.DataEpoch, CompanionHeaders.DataEpoch);
        Assert.Equal(CoreRoutes.CompanionGet, new[] { CompanionRoutes.PrivacyStatus, CompanionRoutes.VoiceStatus, CompanionRoutes.Devices, CompanionRoutes.DeviceCapabilities });
        Assert.Equal(CoreRoutes.CompanionPost, new[] { CompanionRoutes.Devices, CompanionRoutes.VoicePrepare, CompanionRoutes.VoiceWakeCandidates,
            CompanionRoutes.VoiceTickets, CompanionRoutes.VoiceLocalRelease, CompanionRoutes.DeviceHeartbeat });
        Assert.Equal(CoreRoutes.VoiceStream, CompanionRoutes.VoiceStream);
    }

    [Fact]
    public void Core_advertises_lock_aware_protocol_even_when_voice_is_unconfigured()
    {
        using var factory = new JarvisApiFactory(); var ai = factory.Services.GetRequiredService<ManagedAi>();
        var status = ai.Status("Voice"); Assert.False(status.Ready); Assert.Equal(CompanionVoiceProtocol.Version, status.VoiceProtocolVersion);
        Assert.Equal(0, ai.Status("Transcription").VoiceProtocolVersion);
    }

    [Fact]
    public async Task Scoped_enrolment_refresh_exposes_only_its_own_device_and_configured_display_name()
    {
        using var factory = new ProviderFactory(_ => throw new InvalidOperationException("No provider call."));
        factory.Services.GetRequiredService<IConfiguration>()[ConfigKeys.OwnerDisplayName] = "Casey";
        var devices = factory.Services.GetRequiredService<DeviceService>();
        foreach (var id in new[] { "name-laptop", "other-phone" })
            await devices.RegisterAsync(new(id, id, DeviceKind.WindowsAgent, new(true, true, false, false, [CompanionVoiceProtocol.AudioOnly])));
        var issued = await factory.Services.GetRequiredService<DeviceCredentials>().IssueAsync("name-laptop", default);
        using var client = factory.CreateClient(); client.DefaultRequestHeaders.Add(CompanionHeaders.ApiKey, issued.Secret);
        var response = (await client.GetFromJsonAsync<DeviceView[]>(CompanionRoutes.Devices, LifeScenarioTests.Json))!;
        var own = Assert.Single(response); Assert.Equal("name-laptop", own.Device.Registration.DeviceId); Assert.Equal("Casey", own.OwnerDisplayName);
    }

    [Fact]
    public void Owner_name_defaults_to_generic_and_rejects_unbounded_or_control_text()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection().Build();
        Assert.Equal("", OwnerDisplayName.Read(config));
        foreach (var invalid in new[] { new string('x', 81), "Ca\nsey" }) {
            config[ConfigKeys.OwnerDisplayName] = invalid;
            Assert.Equal(503, Assert.Throws<DomainException>(() => OwnerDisplayName.Read(config)).StatusCode);
        }
    }

    [Fact]
    public async Task Simultaneous_Windows_and_Android_wakes_grant_only_one_ticket_without_provider_calls()
    {
        using var factory = new ProviderFactory(_ => throw new InvalidOperationException("Arbitration must not dispatch a paid request."));
        var services = factory.Services; var devices = services.GetRequiredService<DeviceService>();
        foreach (var (id, kind) in new[] { ("wake-laptop", DeviceKind.WindowsAgent), ("wake-phone", DeviceKind.AndroidCompanion) }) {
            await devices.RegisterAsync(new(id, "Audio fixture", kind, new(true, true, false, false, [CompanionVoiceProtocol.AudioOnly])));
            await devices.VerifyAsync(id);
        }
        using var laptop = factory.Client(); using var phone = factory.Client();
        var responses = await Task.WhenAll(laptop.PostAsJsonAsync(CompanionRoutes.VoiceWakeCandidates, new WakeCandidateRequest(Guid.NewGuid(), "wake-laptop", true, false)),
            phone.PostAsJsonAsync(CompanionRoutes.VoiceWakeCandidates, new WakeCandidateRequest(Guid.NewGuid(), "wake-phone", true)));
        try {
            var winner = Assert.Single(responses, x => x.IsSuccessStatusCode);
            Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
            var ticket = (await winner.Content.ReadFromJsonAsync<WakeCandidateResponse>())!.Voice;
            var state = await services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.AudioLease);
            Assert.Equal(ticket.Lease, state); Assert.Single(await services.GetRequiredService<ISessionReader>().ListPreparationAsync(default));
            Assert.Empty(await services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.UsageReservations));
        } finally { foreach (var response in responses) response.Dispose(); }
    }
}
