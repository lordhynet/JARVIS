using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Api.Integrations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed class StubHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => handle(request);
}

public sealed class ProviderFactory(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        TestBudgetSource.Configure(builder); builder.UseEnvironment("Testing"); builder.UseSetting("Jarvis:ApiKey", JarvisApiFactory.TestKey);
        builder.UseSetting("Jarvis:StorageProvider", "InMemory");
        builder.UseSetting("OpenAI:ApiKey", "fake-provider-key"); builder.UseSetting("OpenAI:Model", "test-model");
        builder.UseSetting("OpenAI:TranscriptionModel", "test-transcription");
        builder.UseSetting("AI:Chat:Model", "test-model");
        builder.UseSetting("AI:Chat:RateCardModel", "test-model");
        builder.UseSetting("AI:Transcription:Model", "test-transcription");
        builder.UseSetting("AI:Transcription:RateCardModel", "test-transcription");
        builder.UseSetting("Calendar:Google:AccessToken", "fake-google-token");
        builder.UseSetting("Calendar:Microsoft:AccessToken", "fake-microsoft-token");
        builder.UseSetting("HomeAssistant:BaseUrl", "https://homeassistant.test:8123");
        builder.UseSetting("HomeAssistant:Token", "fake-ha-token");
        builder.UseSetting("HomeAssistant:AllowedEntities:0", "light.desk");
        builder.ConfigureServices(s => s.AddHttpClient("providers").ConfigurePrimaryHttpMessageHandler(() => new StubHttpHandler(handle)));
        builder.ConfigureServices(s => s.AddSingleton<IAudioDurationProbe, FixtureAudioProbe>());
    }
    public HttpClient Client()
    {
        var client = CreateClient(); client.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey); TestOwnerManagement.Authorize(client); return client;
    }
}

internal sealed class FixtureAudioProbe : IAudioDurationProbe
{
    public Task<double> SecondsAsync(string path, CancellationToken ct) => Task.FromResult(1d);
    public Task<PreparedAudio> PrepareAsync(string path, CancellationToken ct)
    {
        var normalized = Path.Combine(Path.GetTempPath(), "jarvis-fixture-" + Guid.NewGuid() + ".wav");
        using (var output = File.Create(normalized)) AudioDurationProbe.WriteWave(output, new byte[32000]);
        return Task.FromResult(new PreparedAudio(normalized, 1d));
    }
}

public sealed class ProviderIntegrationTests
{
    private static HttpResponseMessage Reply(string json, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Coaching_sends_correct_role_context_without_tools_and_extracts_response_text()
    {
        using var host = new ProviderFactory(async request =>
        {
            Assert.Equal("https://api.openai.com/v1/responses", request.RequestUri!.AbsoluteUri);
            Assert.Equal("fake-provider-key", request.Headers.Authorization!.Parameter);
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>();
            Assert.False(body.GetProperty("store").GetBoolean());
            Assert.Contains("Socratically", body.GetProperty("instructions").GetString());
            Assert.False(body.TryGetProperty("tools", out _));
            Assert.Equal("Explain Two Sum", body.GetProperty("input").EnumerateArray().Last().GetProperty("content").GetString());
            return Reply("""{"status":"completed","output":[{"type":"reasoning"},{"type":"message","content":[{"type":"output_text","text":"Which data structure speeds up lookup?"}]}]}""");
        });
        using var client = host.Client(); var session = await LifeScenarioTests.Session(client);
        var result = await LifeScenarioTests.Post<ConversationSession>(client, $"/api/sessions/{session.Id}/messages", new ChatRequest(Guid.NewGuid(), "Explain Two Sum"));
        Assert.Equal("Which data structure speeds up lookup?", result.Messages!.Last().Text);
    }

    [Fact]
    public async Task Transcription_uses_multipart_audio_and_returns_reviewable_text()
    {
        using var host = new ProviderFactory(async request =>
        {
            Assert.EndsWith("/audio/transcriptions", request.RequestUri!.AbsoluteUri);
            Assert.StartsWith("multipart/form-data", request.Content!.Headers.ContentType!.ToString());
            var content = await request.Content.ReadAsStringAsync(); Assert.Contains("test-transcription", content); Assert.Contains("audio.wav", content); Assert.Contains("RIFF", content);
            return Reply("""{"text":"Bonjour JARVIS"}""");
        });
        using var client = host.Client(); using var form = new MultipartFormDataContent(); form.Add(new ByteArrayContent([1, 2, 3]), "audio", "voice.webm");
        var response = await client.PostAsync("/api/voice/transcribe", form); response.EnsureSuccessStatusCode();
        Assert.Equal("Bonjour JARVIS", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("text").GetString());
    }

    [Fact]
    public async Task Unsupported_audio_is_rejected_before_provider_call()
    {
        var called = false; using var host = new ProviderFactory(_ => { called = true; return Task.FromResult(Reply("{}")); });
        using var client = host.Client(); using var form = new MultipartFormDataContent(); form.Add(new ByteArrayContent([1]), "audio", "bad.exe");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/api/voice/transcribe", form)).StatusCode); Assert.False(called);
    }

    [Fact]
    public async Task Google_calendar_follows_pages_preserves_all_day_dates_and_skips_cancelled_events()
    {
        var calls = 0;
        using var host = new ProviderFactory(request =>
        {
            Assert.Equal("www.googleapis.com", request.RequestUri!.Host); Assert.Equal("fake-google-token", request.Headers.Authorization!.Parameter);
            calls++;
            return Task.FromResult(calls == 1
                ? Reply("""{"items":[{"id":"1","summary":"Holiday","start":{"date":"2026-10-30"},"end":{"date":"2026-10-31"}}],"nextPageToken":"next page"}""")
                : Reply("""{"items":[{"id":"2","status":"cancelled"},{"id":"3","summary":"Call","start":{"dateTime":"2026-10-30T10:00:00+00:00"},"end":{"dateTime":"2026-10-30T10:30:00+00:00"}}]}"""));
        });
        using var client = host.Client();
        var events = await client.GetFromJsonAsync<List<CalendarEntry>>("/api/calendar/google?start=2026-10-30T00:00:00Z&end=2026-10-31T00:00:00Z", LifeScenarioTests.Json);
        Assert.Equal(2, calls); Assert.Equal(2, events!.Count); Assert.True(events[0].AllDay); Assert.Equal("2026-10-30", events[0].Start);
    }

    [Fact]
    public async Task Microsoft_calendar_normalizes_utc_and_rejects_untrusted_pagination_host()
    {
        using var host = new ProviderFactory(request =>
        {
            Assert.Equal("graph.microsoft.com", request.RequestUri!.Host); Assert.Equal("fake-microsoft-token", request.Headers.Authorization!.Parameter);
            Assert.Contains("outlook.timezone=\"UTC\"", request.Headers.GetValues("Prefer"));
            return Task.FromResult(Reply("""{"value":[],"@odata.nextLink":"https://attacker.test/steal-token"}"""));
        });
        using var client = host.Client();
        var response = await client.GetAsync("/api/calendar/microsoft?start=2026-10-30T00:00:00Z&end=2026-10-31T00:00:00Z");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task Morning_briefing_survives_calendar_outage_and_keeps_transport_and_learning()
    {
        using var host = new ProviderFactory(request => Task.FromResult(request.RequestUri!.Host == "api.tfl.gov.uk"
            ? Reply("""[{"id":"jubilee","name":"Jubilee","lineStatuses":[{"statusSeverityDescription":"Good Service"}]}]""")
            : Reply("{}", HttpStatusCode.Unauthorized)));
        using var client = host.Client(); var briefing = await client.GetFromJsonAsync<JsonElement>("/api/briefing");
        var sections = briefing.GetProperty("sections").EnumerateArray().ToList();
        Assert.True(sections.Single(s => s.GetProperty("name").GetString() == "TfL").GetProperty("available").GetBoolean());
        Assert.False(sections.Single(s => s.GetProperty("name").GetString() == "Google Calendar").GetProperty("available").GetBoolean());
        Assert.True(sections.Single(s => s.GetProperty("name").GetString() == "Learning due").GetProperty("available").GetBoolean());
    }

    [Fact]
    public async Task Home_action_sends_exact_service_and_entity_only_after_confirmation()
    {
        var calls = 0;
        using var host = new ProviderFactory(async request =>
        {
            calls++; Assert.Equal("https://homeassistant.test:8123/api/services/light/turn_on", request.RequestUri!.AbsoluteUri);
            Assert.Equal("fake-ha-token", request.Headers.Authorization!.Parameter);
            Assert.Equal("light.desk", (await request.Content!.ReadFromJsonAsync<JsonElement>()).GetProperty("entity_id").GetString());
            return Reply("[]");
        });
        using var client = host.Client(); var session = await LifeScenarioTests.Session(client);
        var tool = await LifeScenarioTests.Post<AuditedTool>(client, "/api/tools/home", new HomeActionRequest(Guid.NewGuid(), session.Id, "light.desk", "turn_on"));
        Assert.Equal(0, calls);
        var done = await LifeScenarioTests.Post<AuditedTool>(client, $"/api/tools/{tool.Invocation.Id}/confirm", new { approve = true });
        Assert.Equal(ToolInvocationStatus.Succeeded, done.Invocation.Status); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Unconfigured_model_reports_unavailable_instead_of_fake_success()
    {
        using var original = new JarvisApiFactory();
        using var host = original.WithWebHostBuilder(b => { b.UseSetting("OpenAI:ApiKey", ""); b.UseSetting("OpenAI:Model", ""); });
        using var client = host.CreateClient(); client.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey);
        var session = await LifeScenarioTests.Session(client);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.PostAsJsonAsync($"/api/sessions/{session.Id}/messages", new ChatRequest(Guid.NewGuid(), "Hi"), LifeScenarioTests.Json)).StatusCode);
    }
}
