using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Jarvis.Api.Integrations;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed class AdvancedVoiceHomeTests
{
    private const string Executor = "advanced-home-fixture";

    [Theory]
    [InlineData("set the kitchen light brightness to forty percent", "turn_on", "brightness_pct", "40", "on", """{"brightness":102}""")]
    [InlineData("set the kitchen light brightness to zero percent", "turn_off", null, null, "off", "{}")]
    [InlineData("set the kitchen light colour temperature to 2700 kelvin", "turn_on", "color_temp_kelvin", "2700", "on", """{"color_temp_kelvin":2700}""")]
    [InlineData("set the kitchen light colour to RGB 23 45 67", "turn_on", "rgb_color", "[23,45,67]", "on", """{"rgb_color":[23,45,67]}""")]
    public async Task Voice_home_light_command_checks_the_requested_setting(string speech, string service, string? field,
        string? expected, string state, string attributes)
    {
        var posts = 0;
        using var host = new ProviderFactory(async request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal($"/api/services/light/{service}", request.RequestUri!.AbsolutePath);
                var data = await request.Content!.ReadFromJsonAsync<JsonElement>();
                Assert.Equal("light.kitchen", data.GetProperty("entity_id").GetString());
                Assert.Equal(field is null ? 1 : 2, data.EnumerateObject().Count());
                if (field is not null) Assert.Equal(expected, data.GetProperty(field).GetRawText());
                posts++;
                return Reply("[]");
            }
            Assert.Equal("/api/states/light.kitchen", request.RequestUri!.AbsolutePath);
            return Reply(posts == 0 ? State("light.kitchen", "off", """{"friendly_name":"Kitchen light"}""") :
                State("light.kitchen", state, attributes));
        });
        await ConfigureAsync(host.Services, BuiltInDeviceCapabilities.HomeAssistantLightSet, "light.kitchen");
        using var owner = host.Client();
        var session = await LifeScenarioTests.Session(owner);
        using var voice = CoreInvocationContext.Enter(InvocationOrigin.Voice);
        var result = await SpeakAsync(host.Services, session.Id, speech, "light_set");
        Assert.Equal("succeeded", result.GetProperty("result").GetProperty("status").GetString());
        await RequireReceiptAsync(host.Services, result);
        Assert.Equal(1, posts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Voice_home_scene_requires_a_new_home_assistant_activation_timestamp(bool advances)
    {
        const string baseline = "2026-11-16T12:00:00.100+00:00";
        var posts = 0;
        using var host = new ProviderFactory(request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("/api/services/scene/turn_on", request.RequestUri!.AbsolutePath);
                posts++;
                return Task.FromResult(Reply("[]"));
            }
            Assert.Equal("/api/states/scene.movie_night", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Reply(State("scene.movie_night", advances && posts > 0
                ? "2026-11-16T12:00:00.102+00:00" : baseline, """{"friendly_name":"Movie night"}""")));
        });
        await ConfigureAsync(host.Services, BuiltInDeviceCapabilities.HomeAssistantSceneActivate, "scene.movie_night");
        using var owner = host.Client();
        var session = await LifeScenarioTests.Session(owner);
        using var voice = CoreInvocationContext.Enter(InvocationOrigin.Voice);
        var source = await VoiceHomeTests.SayAsync(host.Services, session.Id, "activate the movie night");
        var result = await VoiceHomeTests.ToolAsync(host.Services, session.Id, source, source, action: "scene");
        Assert.Equal(advances, result.GetProperty("ok").GetBoolean());
        if (advances) await RequireReceiptAsync(host.Services, result);
        var repeated = await VoiceHomeTests.ToolAsync(host.Services, session.Id, source, source, action: "scene");
        if (!advances) Assert.Equal("unverified", repeated.GetProperty("result").GetProperty("status").GetString());
        Assert.Equal(1, posts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Voice_home_climate_fences_the_owner_range_and_verifies_the_target_in_home_assistant_units(bool stateHasUnit)
    {
        var posts = 0;
        var configReads = 0;
        using var host = new ProviderFactory(async request =>
        {
            if (request.Method == HttpMethod.Post)
            {
                Assert.Equal("/api/services/climate/set_temperature", request.RequestUri!.AbsolutePath);
                var data = await request.Content!.ReadFromJsonAsync<JsonElement>();
                Assert.Equal(70.7m, data.GetProperty("temperature").GetDecimal());
                Assert.Equal("climate.hall", data.GetProperty("entity_id").GetString());
                Assert.False(data.TryGetProperty("hvac_mode", out _));
                posts++;
                return Reply("[]");
            }
            if (request.RequestUri!.AbsolutePath == "/api/config")
            {
                configReads++;
                return Reply("""{"unit_system":{"temperature":"°F"}}""");
            }
            Assert.Equal("/api/states/climate.hall", request.RequestUri.AbsolutePath);
            var attributes = new Dictionary<string, object> { ["friendly_name"] = "Hall thermostat",
                ["temperature"] = posts == 0 ? 66m : 70.7m, ["min_temp"] = 50, ["max_temp"] = 80 };
            if (stateHasUnit) attributes["temperature_unit"] = "°F";
            return Reply(State("climate.hall", "heat", JsonSerializer.Serialize(attributes)));
        });
        await ConfigureAsync(host.Services, BuiltInDeviceCapabilities.HomeAssistantClimateSet, "climate.hall", min: 18, max: 22);
        using var owner = host.Client();
        var session = await LifeScenarioTests.Session(owner);
        using var voice = CoreInvocationContext.Enter(InvocationOrigin.Voice);
        var denied = await SpeakAsync(host.Services, session.Id, "set the hall thermostat to 23 degrees", "climate_set");
        Assert.False(denied.GetProperty("ok").GetBoolean());
        Assert.Equal(0, posts);
        var result = await SpeakAsync(host.Services, session.Id, "set the hall thermostat to 21.5 degrees Celsius", "climate_set");
        Assert.Equal("succeeded", result.GetProperty("result").GetProperty("status").GetString());
        await RequireReceiptAsync(host.Services, result);
        Assert.Equal(1, posts);
        Assert.Equal(stateHasUnit ? 0 : 2, configReads);
    }

    private static async Task ConfigureAsync(IServiceProvider services, string capability, string entity, decimal? min = null, decimal? max = null)
    {
        var (_, secret) = await DeviceCommandTests.SetupAsync(services, Executor);
        var config = services.GetRequiredService<IConfiguration>();
        config[HomeAssistantCommandAdapter.ExecutorDeviceId] = Executor;
        config[HomeAssistantCommandAdapter.ExecutorCredential] = secret;
        config["HomeAssistant:AllowedEntities:0"] = entity;
        var constraints = new Dictionary<string, object> { ["allowedEntityIds"] = new[] { entity } };
        if (min is { } lower && max is { } upper)
        {
            constraints["minC"] = lower;
            constraints["maxC"] = upper;
        }
        await services.GetRequiredService<DeviceGrantService>().GrantAsync(Executor,
            DeviceGrantTests.GrantRequest() with { CapabilityId = capability, ConstraintsJson = JsonSerializer.Serialize(constraints), PreApproved = true },
            new("fixture-owner", [DeviceGrantService.ManageScope]));
    }

    private static async Task<JsonElement> SpeakAsync(IServiceProvider services, Guid session, string speech, string action)
    {
        var source = await VoiceHomeTests.SayAsync(services, session, speech);
        return await VoiceHomeTests.ToolAsync(services, session, source, source, action: action);
    }

    private static async Task RequireReceiptAsync(IServiceProvider services, JsonElement result)
    {
        var id = result.GetProperty("result").GetProperty("commandId").GetGuid();
        var receipts = await services.GetRequiredService<IDeviceReceiptReader>().ListAsync(id);
        Assert.Equal(DeviceReceiptKind.Succeeded, receipts[1].Kind);
        Assert.False(receipts[1].Late);
    }

    private static HttpResponseMessage Reply(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static string State(string entity, string state, string attributes) =>
        $$"""{"entity_id":"{{entity}}","state":"{{state}}","attributes":{{attributes}}}""";
}
