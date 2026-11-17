using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jarvis.Api.Integrations;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Intelligence;
using Jarvis.Orchestrator.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed class AnthropicModernisationTests
{
    private const string Model = "claude-sonnet-5-5";
    private const string Fallback = "claude-sonnet-5";
    private static TextReasoningTask Request() => new(Guid.NewGuid(), Guid.NewGuid(), "Chat", TextCapability.Conversation,
        ReasoningDataClass.Personal, true, "Stable instructions.", [new("user", "private changing context")], UsageLane.Interactive);
    private static void Configure(IWebHostBuilder builder, bool caching = false, bool fallback = false)
    {
        builder.UseSetting("Anthropic:ApiKey", "fixture-anthropic-key");
        builder.UseSetting("AI:Chat:Provider", "anthropic");
        builder.UseSetting("AI:Chat:Model", Model); builder.UseSetting("AI:Chat:RateCardModel", Model);
        builder.UseSetting("AI:Chat:AllowedDataClasses:0", "Personal");
        builder.UseSetting("AI:Chat:InputUsdPerMillion", "2"); builder.UseSetting("AI:Chat:OutputUsdPerMillion", "4");
        builder.UseSetting("AI:Chat:PromptCachingEnabled", caching.ToString());
        builder.UseSetting("AI:Chat:CacheWriteUsdPerMillion", "3"); builder.UseSetting("AI:Chat:CacheReadUsdPerMillion", "0.2");
        if (!fallback) return;
        builder.UseSetting("AI:Chat:Fallbacks:0:Model", Fallback); builder.UseSetting("AI:Chat:Fallbacks:0:RateCardModel", Fallback);
        builder.UseSetting("AI:Chat:Fallbacks:0:RateCardDate", DateTime.UtcNow.ToString("yyyy-MM-dd"));
        builder.UseSetting("AI:Chat:Fallbacks:0:InputUsdPerMillion", "5"); builder.UseSetting("AI:Chat:Fallbacks:0:OutputUsdPerMillion", "20");
        builder.UseSetting("AI:Chat:Fallbacks:0:CacheWriteUsdPerMillion", "6"); builder.UseSetting("AI:Chat:Fallbacks:0:CacheReadUsdPerMillion", "0.5");
    }
    private static HttpResponseMessage Response(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private static object Usage(int input = 10, int output = 10, int write = 0, int read = 0) =>
        new { input_tokens = input, output_tokens = output, cache_creation_input_tokens = write, cache_read_input_tokens = read };
    private static object Message(string stop = "end_turn", string model = Model, object? usage = null) =>
        new { model, stop_reason = stop, usage = usage ?? Usage(), content = new[] { new { type = "text", text = "answer" } } };

    [Fact]
    public async Task Thinking_output_is_reserved_and_only_visible_text_is_returned()
    {
        IRuntimeStore? state = null;
        using var root = new ProviderFactory(async request => {
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(8192, body.GetProperty("max_tokens").GetInt32());
            Assert.Equal(AnthropicMessagesAdapter.ApiVersion, Assert.Single(request.Headers.GetValues("anthropic-version")));
            Assert.False(request.Headers.Contains("anthropic-beta")); Assert.False(body.TryGetProperty("fallbacks", out _));
            Assert.False(body.TryGetProperty("thinking", out _));
            var held = Assert.Single(await state!.ReadAsync(s => s.UsageReservations.Values.ToArray()));
            Assert.True(held.ReservedUsd >= 8192 * 4m / 1_000_000);
            return Response(new { model = Model, stop_reason = "end_turn", usage = Usage(output: 5000),
                content = new[] { new { type = "thinking", text = "private reasoning" }, new { type = "text", text = "answer" } } });
        });
        using var host = root.WithWebHostBuilder(b => Configure(b));
        state = host.Services.GetRequiredService<IRuntimeStore>();
        var result = await host.Services.GetRequiredService<ITextReasoningProvider>().ReasonAsync(Request(), default);
        Assert.Equal("answer", result.Text);
        var charge = Assert.Single(await state.ReadAsync(s => s.UsageReservations.Values.ToArray()));
        Assert.Equal(.02002m, charge.ActualUsd); Assert.Equal(5000, charge.OutputTokens);
    }

    [Theory]
    [InlineData("Chat", 8192)]
    [InlineData("Coach", 8192)]
    [InlineData("Agent", 16384)]
    [InlineData("LegacyInterview", 16384)]
    public void Profiles_have_thinking_sized_defaults_and_honour_explicit_caps(string profile, int expected)
    {
        using var root = new ProviderFactory(_ => throw new InvalidOperationException("No dispatch"));
        using var host = root.WithWebHostBuilder(b => {
            Configure(b);
            var prefix = profile == "LegacyInterview" ? "PaidAI:" : $"AI:{profile}:";
            b.UseSetting(prefix + "Provider", "anthropic"); b.UseSetting(prefix + "Model", Model);
            b.UseSetting("Anthropic:Model", Model); b.UseSetting("PaidAI:Enabled", "true");
            b.UseSetting(prefix + "RateCardModel", Model); b.UseSetting(prefix + "RateCardDate", DateTime.UtcNow.ToString("yyyy-MM-dd"));
            b.UseSetting(prefix + "InputUsdPerMillion", "2"); b.UseSetting(prefix + "OutputUsdPerMillion", "4");
            b.UseSetting(prefix + "AllowedDataClasses:0", "Personal");
        });
        var cards = host.Services.GetRequiredService<RateCard>();
        Assert.Equal(expected, cards.ReadText(profile, TextCapability.JsonReasoning, ReasoningDataClass.Personal).Route.OutputLimit);
        Assert.Equal(4096, cards.ReadText(profile, TextCapability.JsonReasoning, ReasoningDataClass.Personal, 4096).Route.OutputLimit);
        Assert.Equal(400, Assert.Throws<DomainException>(() => cards.ReadText(profile, TextCapability.JsonReasoning, ReasoningDataClass.Personal, expected + 1)).StatusCode);
    }

    [Theory]
    [InlineData("max_tokens", "output cap")]
    [InlineData("refusal", "refused")]
    [InlineData("pause_turn", "incomplete")]
    public async Task Incomplete_responses_settle_but_never_return_partial_text_or_replay(string stop, string expected)
    {
        var calls = 0;
        using var root = new ProviderFactory(_ => { calls++; return Task.FromResult(Response(Message(stop))); });
        using var host = root.WithWebHostBuilder(b => Configure(b));
        var service = host.Services.GetRequiredService<ITextReasoningProvider>(); var request = Request();
        var error = await Assert.ThrowsAsync<DomainException>(() => service.ReasonAsync(request, default));
        Assert.Equal(502, error.StatusCode); Assert.Contains(expected, error.Message);
        var state = host.Services.GetRequiredService<IRuntimeStore>();
        Assert.Equal(.00006m, (await state.ReadAsync(s => s.UsageReservations[request.OperationId])).ActualUsd);
        var attempt = await state.ReadAsync(s => s.ProviderAttempts[request.OperationId]);
        Assert.Equal("Failed", attempt.Outcome); Assert.Equal(Model, attempt.ServedRoute!.Model);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => service.ReasonAsync(request, default))).StatusCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Priced_cache_uses_only_stable_prefix_and_is_charged_separately_from_normal_input()
    {
        var calls = 0;
        using var root = new ProviderFactory(async request => {
            calls++;
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>();
            var block = Assert.Single(body.GetProperty("system").EnumerateArray());
            Assert.Equal("Stable instructions.", block.GetProperty("text").GetString());
            Assert.Equal("5m", block.GetProperty("cache_control").GetProperty("ttl").GetString());
            Assert.DoesNotContain("cache_control", body.GetProperty("messages").GetRawText());
            return Response(Message(usage: Usage(10, 2, 20, 30)));
        });
        using var host = root.WithWebHostBuilder(b => Configure(b, caching: true));
        var service = host.Services.GetRequiredService<ITextReasoningProvider>(); var request = Request();
        await service.ReasonAsync(request, default); await service.ReasonAsync(request, default);
        var state = host.Services.GetRequiredService<IRuntimeStore>();
        var charge = await state.ReadAsync(s => s.UsageReservations[request.OperationId]);
        Assert.Equal(.000094m, charge.ActualUsd); Assert.Equal(60, charge.InputTokens); Assert.False(charge.ReconciliationRequired);
        var usage = Assert.Single((await state.ReadAsync(s => s.ProviderAttempts[request.OperationId])).ModelUsage!);
        Assert.Equal(20, usage.CacheWriteTokens); Assert.Equal(30, usage.CacheReadTokens); Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("cyber", 0, 0)]
    [InlineData("bio", 0, 86)]
    [InlineData("cyber", 2, 94)]
    public async Task Fallback_records_served_route_and_every_billable_hop_using_dispatch_snapshot(string category, int declinedOutput, int declinedMicroUsd)
    {
        var calls = 0; IConfiguration? config = null; IRuntimeStore? state = null;
        using var root = new ProviderFactory(async request => {
            calls++;
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(AnthropicMessagesAdapter.FallbackBeta, Assert.Single(request.Headers.GetValues("anthropic-beta")));
            Assert.Equal(Fallback, Assert.Single(body.GetProperty("fallbacks").EnumerateArray()).GetProperty("model").GetString());
            var held = Assert.Single(await state!.ReadAsync(s => s.UsageReservations.Values.ToArray()));
            Assert.True(held.ReservedUsd >= (8192 * (4m + 20m)) / 1_000_000);
            config!["AI:Chat:Fallbacks:0:InputUsdPerMillion"] = "999";
            return Response(new { model = Fallback, stop_reason = "end_turn", usage = new {
                input_tokens = 20, output_tokens = 40, cache_creation_input_tokens = 100, cache_read_input_tokens = 200,
                iterations = new[] {
                    new { type = "message", model = Model, input_tokens = 10, output_tokens = declinedOutput, cache_creation_input_tokens = 20, cache_read_input_tokens = 30 },
                    new { type = "fallback_message", model = Fallback, input_tokens = 20, output_tokens = 40, cache_creation_input_tokens = 100, cache_read_input_tokens = 200 }
                } }, content = new object[] { new { type = "fallback", from = new { model = Model }, to = new { model = Fallback }, trigger = new { type = "refusal", category } },
                    new { type = "text", text = "fallback answer" } } });
        });
        using var host = root.WithWebHostBuilder(b => Configure(b, true, true));
        config = host.Services.GetRequiredService<IConfiguration>(); state = host.Services.GetRequiredService<IRuntimeStore>();
        var service = host.Services.GetRequiredService<ITextReasoningProvider>(); var request = Request();
        var result = await service.ReasonAsync(request, default);
        Assert.Equal("fallback answer", result.Text); Assert.Equal(Fallback, result.Route.Model); Assert.Equal(5m, result.Route.InputUsdPerMillion);
        var charge = await state.ReadAsync(s => s.UsageReservations[request.OperationId]);
        Assert.Equal(.0016m + declinedMicroUsd / 1_000_000m, charge.ActualUsd); Assert.False(charge.ReconciliationRequired);
        var attempt = await state.ReadAsync(s => s.ProviderAttempts[request.OperationId]);
        Assert.Equal(Model, attempt.Route.Model); Assert.Equal(Fallback, attempt.ServedRoute!.Model); Assert.Equal(2, attempt.ModelUsage!.Count);
        config["AI:Chat:Fallbacks:0:InputUsdPerMillion"] = "5";
        var cached = await service.ReasonAsync(request, default); Assert.Equal(Fallback, cached.Route.Model); Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("unpriced-cache")]
    [InlineData("unknown-model")]
    [InlineData("unpriced-hour-cache")]
    [InlineData("negative-usage")]
    [InlineData("missing-fallback-iterations")]
    public async Task Unreviewed_usage_retains_reservation_and_blocks_new_paid_work(string scenario)
    {
        var calls = 0;
        using var root = new ProviderFactory(_ => {
            calls++;
            var usage = scenario == "unpriced-hour-cache" ? (object)new { input_tokens = 10, output_tokens = 2,
                cache_creation_input_tokens = 20, cache_creation = new { ephemeral_5m_input_tokens = 0, ephemeral_1h_input_tokens = 20 } } :
                Usage(input: scenario == "negative-usage" ? -1 : 10, write: scenario == "unpriced-cache" ? 10 : 0);
            return Task.FromResult(Response(Message(model: scenario == "unknown-model" ? "unreviewed-model" : scenario == "missing-fallback-iterations" ? Fallback : Model, usage: usage)));
        });
        using var host = root.WithWebHostBuilder(b => {
            Configure(b, fallback: scenario == "missing-fallback-iterations");
            if (scenario == "unpriced-cache") b.UseSetting("AI:Chat:CacheWriteUsdPerMillion", "");
        });
        var service = host.Services.GetRequiredService<ITextReasoningProvider>(); var request = Request();
        Assert.Equal(502, (await Assert.ThrowsAsync<DomainException>(() => service.ReasonAsync(request, default))).StatusCode);
        var charge = await host.Services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.UsageReservations[request.OperationId]);
        Assert.Null(charge.ActualUsd); Assert.True(charge.ReconciliationRequired);
        Assert.Equal(503, (await Assert.ThrowsAsync<DomainException>(() => service.ReasonAsync(Request(), default))).StatusCode);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("AI:Chat:CacheWriteUsdPerMillion", "")]
    [InlineData("AI:Chat:Fallbacks:0:OutputUsdPerMillion", "0")]
    [InlineData("AI:Chat:Fallbacks:0:RateCardDate", "2000-01-01")]
    [InlineData("AI:Chat:Fallbacks:0:Model", Model)]
    public async Task Invalid_cache_or_fallback_card_fails_before_reservation_or_dispatch(string key, string value)
    {
        using var root = new ProviderFactory(_ => throw new InvalidOperationException("No dispatch permitted"));
        using var host = root.WithWebHostBuilder(b => { Configure(b, true, true); b.UseSetting(key, value); });
        Assert.Equal(503, (await Assert.ThrowsAsync<DomainException>(() => host.Services.GetRequiredService<ITextReasoningProvider>().ReasonAsync(Request(), default))).StatusCode);
        Assert.Empty(await host.Services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.UsageReservations));
    }
}
