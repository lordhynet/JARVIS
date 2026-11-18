using System.Net;
using System.Text;
using System.Text.Json;
using Jarvis.Api.Integrations;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Travel;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Jarvis.Tests;

public sealed class TflTravelSourceTests
{
    private sealed class Clients(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) { Assert.Equal(TflTravelSource.ClientName, name); return new(new StubHttpHandler(send)) { MaxResponseContentBufferSize = 2_000_000 }; }
    }
    private sealed class Logs : ILogger<TflTravelSource>
    {
        public List<string> Lines = []; public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format) => Lines.Add(format(state, error));
    }
    private static HttpResponseMessage Reply(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static TflTravelSource Source(TravelClock clock, Func<HttpRequestMessage, Task<HttpResponseMessage>> send, Logs? logs = null) => new(new Clients(send),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["TfL:AppKey"] = "synthetic-secret+query" }).Build(), clock, logs ?? new());
    [Fact]
    public async Task Catalogue_is_cached_until_the_next_London_day_and_the_entire_query_is_redacted()
    {
        var clock = new TravelClock { Now = DateTimeOffset.Parse("2026-07-06T22:59:00Z") }; var calls = 0; var logs = new Logs();
        var source = Source(clock, request => { calls++; Assert.Contains("app_key=", request.RequestUri!.Query); return Task.FromResult(Reply(new[] { new { id = "jubilee", name = "Jubilee", modeName = "tube" } })); }, logs);
        await source.CatalogueAsync(default); await source.CatalogueAsync(default); Assert.Equal(1, calls);
        clock.Now = clock.Now.AddMinutes(2); await source.CatalogueAsync(default); Assert.Equal(2, calls);
        Assert.All(logs.Lines, line => { Assert.DoesNotContain("synthetic-secret", line); Assert.DoesNotContain("app_key", line); Assert.Contains("?[redacted]", line); });
        Assert.Equal("https://api.tfl.gov.uk/Line/jubilee/Status?[redacted]", TflTravelSource.SafeUrl(new("https://api.tfl.gov.uk/Line/jubilee/Status?app_key=private&another=hidden")));
    }
    [Fact]
    public async Task Large_union_uses_one_public_modes_batch_without_exposing_route_data()
    {
        var clock = new TravelClock(); var calls = 0;
        var source = Source(clock, request => { calls++; Assert.Equal("/Line/Mode/tube/Status", request.RequestUri!.AbsolutePath); return Task.FromResult(Reply(new[] {
            new { id = "line1", name = "Public line", lineStatuses = new[] { new { statusSeverity = 10, statusSeverityDescription = "Good Service" } } }
        })); });
        var lines = Enumerable.Range(1, 120).Select(i => new TravelLine("line" + i, "Public " + i, "tube")).ToArray();
        Assert.Single(await source.StatusAsync(lines, default)); Assert.Equal(1, calls);
    }
    [Fact]
    public async Task Arrivals_are_filtered_to_the_saved_stop_lines_sorted_and_capped_at_five()
    {
        var clock = new TravelClock(); var source = Source(clock, request => {
            Assert.Equal("/StopPoint/940GZZLUTST/Arrivals", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Reply(Enumerable.Range(0, 10).Reverse().Select(i => new { lineId = i == 0 ? "northern" : "jubilee", lineName = "Jubilee", destinationName = "Public destination", platformName = "Platform 1", expectedArrival = clock.Now.AddMinutes(i).ToString("O") }).ToArray()));
        });
        var arrivals = await source.ArrivalsAsync(new("940GZZLUTST", "Public stop", ["jubilee"]), default);
        Assert.Equal(5, arrivals.Count); Assert.Equal(60, arrivals[0].Seconds); Assert.Equal(300, arrivals[^1].Seconds);
        await Assert.ThrowsAsync<DomainException>(() => source.ArrivalsAsync(new("https://evil.invalid", "Bad", []), default));
    }
    [Fact]
    public async Task Missing_status_is_unavailable_and_a_redirect_is_never_accepted_as_transport_data()
    {
        var clock = new TravelClock(); var source = Source(clock, _ => Task.FromResult(Reply(new[] { new { id = "jubilee", lineStatuses = Array.Empty<object>() } })));
        await Assert.ThrowsAsync<DomainException>(() => source.StatusAsync([FakeTravelSource.Jubilee], default));
        var redirected = Source(clock, _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new("https://other.invalid/") } }));
        await Assert.ThrowsAsync<DomainException>(() => redirected.CatalogueAsync(default));
    }
    [Theory]
    [InlineData("25:00", "09:00", "Europe/London")]
    [InlineData("08:00", "08:00", "Europe/London")]
    [InlineData("08:00", "09:00", "UTC")]
    public void Invalid_windows_and_timezone_are_refused(string start, string end, string zone) => Assert.Throws<DomainException>(() => TravelPolicy.Validate(
        new(Guid.NewGuid(), null, "Fixture", ["jubilee"], [], [new([DayOfWeek.Monday], start, end)], TimeZone: zone)));
    [Fact]
    public async Task Unknown_line_and_twelve_line_limit_are_checked_before_a_route_is_published()
    {
        var rig = new TravelRig(); var request = new SaveTravelRouteRequest(Guid.NewGuid(), null, "Fixture", ["unknown"], [], [new([DayOfWeek.Monday], "08:00", "09:00")]);
        await Assert.ThrowsAsync<DomainException>(() => rig.Service.SaveAsync(null, request)); Assert.Empty(await rig.Service.ListAsync());
        await Assert.ThrowsAsync<DomainException>(() => rig.Service.SaveAsync(null, request with { LineIds = Enumerable.Range(1, 13).Select(i => "line" + i).ToArray() }));
        Assert.Empty(await rig.Service.ListAsync());
    }
}
