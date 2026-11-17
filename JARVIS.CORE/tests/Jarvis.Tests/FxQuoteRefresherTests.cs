using System.Net;
using System.Text;
using Jarvis.Api.Integrations;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Jarvis.Tests;

public sealed class FxQuoteRefresherTests
{
    [Fact]
    public async Task Refresh_keeps_billing_available_over_a_72_hour_weekend_and_snapshots_each_rate()
    {
        using var fixture = new Fixture();
        fixture.Feed.Reply = () => Xml("2026-11-13", "1.0", usd: "1.25");
        await fixture.Refresh();
        var first = fixture.Source.Read();
        Assert.Equal(.816m, first.Fx!.UsdToGbp);
        Assert.Equal(new DateOnly(2026, 11, 13), first.Fx.PublishedOn);
        Assert.Equal(TimeSpan.FromHours(24), first.Fx.ExpiresAt - first.Fx.ObservedAt);
        Assert.Contains("+2% margin", first.Fx.Source);
        var firstCharge = await fixture.Reserve();

        for (var hours = 6; hours <= 72; hours += 6)
        {
            fixture.Clock.Now = Friday.AddHours(hours);
            fixture.Budget.RequireConfigured();
            if (hours == 72) fixture.Feed.Reply = () => Xml("2026-11-16", "1.025", usd: "1.25");
            await fixture.Refresh();
            fixture.Budget.RequireConfigured();
        }

        var second = fixture.Source.Read();
        Assert.Equal(.8364m, second.Fx!.UsdToGbp);
        Assert.Equal(GbpBudgetRules.PolicyHash(first), GbpBudgetRules.PolicyHash(second));
        Assert.NotEqual(GbpBudgetRules.ConversionHash(first), GbpBudgetRules.ConversionHash(second));
        var secondCharge = await fixture.Reserve();
        var commitments = await fixture.Store.ReadAsync(s => s.GbpCommitments);
        Assert.Equal(first.Fx, commitments[firstCharge.Id].Fx);
        Assert.Equal(second.Fx, commitments[secondCharge.Id].Fx);
    }

    [Fact]
    public async Task A_jump_retains_the_previous_quote_and_an_expired_quote_fails_closed()
    {
        using var fixture = new Fixture();
        await fixture.Refresh();
        var accepted = fixture.Source.Read().Fx;
        fixture.Clock.Now = Friday.AddHours(6);
        fixture.Feed.Reply = () => Xml("2026-11-13", ".85");
        await fixture.Refresh();
        Assert.Equal(accepted, fixture.Source.Read().Fx);
        Assert.Contains("5%", fixture.Source.Read().FxStatus!.LastError!);
        fixture.Budget.RequireConfigured();

        fixture.Clock.Now = Friday.AddHours(30);
        fixture.Feed.Reply = () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
        await fixture.Refresh();
        var error = await Assert.ThrowsAsync<DomainException>(() => fixture.Reserve());
        Assert.Equal(503, error.StatusCode);
        Assert.Contains("Automatic FX refresh failed", error.Message);
        var view = await new BudgetApprovalService(fixture.Store, fixture.Source, fixture.Clock).StatusAsync();
        Assert.False(view.Ready);
        Assert.Contains("successful response", view.ConversionBasis!.FxStatus!.LastError!);
        Assert.Equal(accepted, await fixture.Store.ReadAsync(s => s.FxRefresh!.Quote));
    }

    [Theory]
    [InlineData("old", "age limit")]
    [InlineData("future", "age limit")]
    [InlineData("malformed", "invalid XML")]
    [InlineData("entity", "invalid XML")]
    [InlineData("oversized", "size limit")]
    [InlineData("missing", "GBP rate")]
    [InlineData("duplicate", "USD rate")]
    [InlineData("range", "GBP rate")]
    [InlineData("redirect", "successful response")]
    public async Task Untrusted_feed_inputs_do_not_create_an_automatic_quote(string scenario, string detail)
    {
        using var fixture = new Fixture();
        fixture.Feed.Reply = scenario switch
        {
            "old" => () => Xml("2026-11-08", ".80"),
            "future" => () => Xml("2026-11-14", ".80"),
            "malformed" => () => Text("<Cube>"),
            "entity" => () => Text("<!DOCTYPE a [<!ENTITY b SYSTEM 'file:///not-allowed'>]><a>&b;</a>"),
            "oversized" => () => Text(new string(' ', 65537), advertisedLength: 1),
            "missing" => () => Xml("2026-11-13", null),
            "duplicate" => () => Xml("2026-11-13", ".80", "<Cube currency='USD' rate='1.0'/>"),
            "range" => () => Xml("2026-11-13", ".29"),
            _ => () => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new("https://redirect.invalid/") } }
        };
        await fixture.Refresh();
        var policy = fixture.Source.Read();
        Assert.Null(policy.Fx);
        Assert.Contains(detail, policy.FxStatus!.LastError!);
        Assert.False(policy.FxStatus.UsingAutomaticQuote);
    }

    [Fact]
    public async Task Manual_valid_quote_takes_precedence_and_disabled_refresh_does_not_fetch()
    {
        using var fixture = new Fixture();
        fixture.Config["Budget:Fx:Source"] = "owner fixture";
        fixture.Config["Budget:Fx:UsdToGbp"] = ".9";
        fixture.Config["Budget:Fx:ObservedAt"] = Friday.AddHours(-1).ToString("O");
        fixture.Config["Budget:Fx:ExpiresAt"] = Friday.AddHours(2).ToString("O");
        fixture.Config["Budget:FxAuto:Enabled"] = "false";
        await fixture.Refresh();
        Assert.Equal(0, fixture.Feed.Requests);
        Assert.Equal("owner fixture", fixture.Source.Read().Fx!.Source);

        fixture.Config["Budget:FxAuto:Enabled"] = "true";
        await fixture.Refresh();
        Assert.False(fixture.Source.Read().FxStatus!.UsingAutomaticQuote);
        var charge = await fixture.Reserve();
        Assert.Equal(.9m, await fixture.Store.ReadAsync(s => s.GbpCommitments[charge.Id].Fx.UsdToGbp));
        fixture.Clock.Now = Friday.AddHours(3);
        Assert.True(fixture.Source.Read().FxStatus!.UsingAutomaticQuote);
        Assert.Equal(.816m, fixture.Source.Read().Fx!.UsdToGbp);
    }

    private static readonly DateTimeOffset Friday = new(2026, 11, 13, 17, 0, 0, TimeSpan.Zero);

    private sealed class Fixture : IDisposable
    {
        public readonly BudgetClock Clock = new() { Now = Friday };
        public readonly IConfigurationRoot Config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Budget:Enabled"] = "true", ["Budget:ScopeId"] = "fx-test", ["Budget:Version"] = "fx-fixture", ["Budget:Environment"] = "Development",
            ["Budget:TaxMultiplier"] = "1", ["Budget:SafetyMultiplier"] = "1", ["Budget:FxAuto:Enabled"] = "true"
        }).Build();
        public readonly InMemoryRuntimeStore Store = new();
        public readonly FeedHandler Feed = new();
        private readonly FxQuoteRefresher refresher;
        public BudgetSettings Source { get; }
        public UsageBudget Budget { get; }

        public Fixture()
        {
            var cache = new FxQuoteCache();
            Source = new(Config, Clock, cache);
            Budget = new(Store, Clock, Source);
            refresher = new(new FeedFactory(Feed), Config, Store, cache, Clock, NullLogger<FxQuoteRefresher>.Instance);
        }

        public Task Refresh() => refresher.RefreshOnceAsync(default);
        public Task<UsageReservation> Reserve() => Budget.ReserveAsync(Guid.NewGuid(), Guid.NewGuid(), "test-model", 1m, new(10, 10, 10), default);
        public void Dispose() { refresher.Dispose(); Feed.Dispose(); }
    }

    private sealed class FeedFactory(FeedHandler feed) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(FxAutoSettings.ClientName, name);
            return new(feed, disposeHandler: false);
        }
    }

    private sealed class FeedHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public Func<HttpResponseMessage> Reply { get; set; } = () => Xml("2026-11-13", ".80");
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(FxQuoteRefresher.SourceUrl, request.RequestUri!.AbsoluteUri);
            Requests++;
            return Task.FromResult(Reply());
        }
    }

    private static HttpResponseMessage Xml(string day, string? gbp, string extra = "", string usd = "1.0")
    {
        var sterling = gbp is null ? "" : $"<Cube currency='GBP' rate='{gbp}'/>";
        return Text($"""
            <gesmes:Envelope xmlns:gesmes="http://www.gesmes.org/xml/2002-08-01" xmlns="http://www.ecb.int/vocabulary/2002-08-01/eurofxref">
              <Cube><Cube time="{day}"><Cube currency="USD" rate="{usd}"/>{sterling}{extra}</Cube></Cube>
            </gesmes:Envelope>
            """);
    }

    private static HttpResponseMessage Text(string body, long? advertisedLength = null)
    {
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        if (advertisedLength is long size) content.Headers.ContentLength = size;
        return new(HttpStatusCode.OK) { Content = content };
    }
}
