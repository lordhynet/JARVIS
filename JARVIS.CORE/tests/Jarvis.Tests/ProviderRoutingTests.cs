using System.Net;
using System.Text;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Intelligence;
using Jarvis.Orchestrator.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed class ProviderRoutingTests
{
    private static HttpResponseMessage Result(string provider) => new(HttpStatusCode.OK) {
        Content = new StringContent(provider == "openai"
            ? """{"status":"completed","usage":{"input_tokens":10,"output_tokens":10},"output":[{"content":[{"type":"output_text","text":"same answer"}]}]}"""
            : """{"stop_reason":"end_turn","usage":{"input_tokens":10,"output_tokens":10},"content":[{"type":"text","text":"same answer"}]}""", Encoding.UTF8, "application/json")
    };
    private static TextReasoningTask Task() => new(Guid.NewGuid(), Guid.NewGuid(), "Chat", TextCapability.Conversation,
        ReasoningDataClass.Personal, true, "Answer concisely using supplied facts.", [new("user", "hello")], UsageLane.Interactive);

    [Theory]
    [InlineData("openai")]
    [InlineData("anthropic")]
    public async System.Threading.Tasks.Task Normalized_task_reserves_records_route_and_reuses_cache_on_each_adapter(string provider)
    {
        var calls = 0;
        using var root = new ProviderFactory(_ => { Interlocked.Increment(ref calls); return System.Threading.Tasks.Task.FromResult(Result(provider)); });
        using var host = root.WithWebHostBuilder(builder => {
            builder.UseSetting("AI:Chat:Provider", provider); builder.UseSetting("Anthropic:ApiKey", "fixture-anthropic-routing-key");
            builder.UseSetting("AI:Chat:AllowedDataClasses:0", "Personal");
            builder.UseSetting("AI:Chat:InputUsdPerMillion", "2"); builder.UseSetting("AI:Chat:OutputUsdPerMillion", "4");
        });
        var service = host.Services.GetRequiredService<ITextReasoningProvider>(); var request = Task();
        var first = await service.ReasonAsync(request, default); var second = await service.ReasonAsync(request, default);
        Assert.Equal("same answer", first.Text); Assert.Equal(first.Text, second.Text); Assert.Equal(provider, first.Route.Provider); Assert.Equal(1, calls);
        var state = host.Services.GetRequiredService<IRuntimeStore>();
        var charge = Assert.Single(await state.ReadAsync(s => s.UsageReservations.Values.ToArray()));
        Assert.Equal(request.OperationId, charge.Id); Assert.Equal(.00006m, charge.ActualUsd); Assert.Equal(provider, charge.Provider);
        var attempt = Assert.Single(await state.ReadAsync(s => s.ProviderAttempts.Values.ToArray()));
        Assert.Equal(charge.Id, attempt.ReservationId); Assert.Equal("Cached", attempt.Outcome); Assert.Equal("test-model", attempt.Route.Model);
        Assert.DoesNotContain("fixture-anthropic-routing-key", await state.ReadAsync(s => JsonSerializer.Serialize(s)));
    }

    [Fact]
    public async System.Threading.Tasks.Task Disallowed_data_and_unsupported_grounding_never_dispatch_or_fall_back()
    {
        var calls = 0;
        using var root = new ProviderFactory(_ => { Interlocked.Increment(ref calls); throw new InvalidOperationException("No dispatch permitted"); });
        using var host = root.WithWebHostBuilder(builder => {
            builder.UseSetting("AI:Chat:AllowedDataClasses:0", "Public");
            builder.UseSetting("AI:Research:Provider", "anthropic"); builder.UseSetting("Anthropic:ApiKey", "fixture-anthropic-routing-key");
            builder.UseSetting("AI:Research:AllowedDataClasses:0", "Public");
        });
        var service = host.Services.GetRequiredService<ITextReasoningProvider>();
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() => service.ReasonAsync(Task(), default))).StatusCode);
        Assert.Equal(403, (await Assert.ThrowsAsync<DomainException>(() => service.ReasonAsync(Task() with {
            Profile = "Research", Capability = TextCapability.GroundedPublicSearch, DataClass = ReasoningDataClass.Public }, default))).StatusCode);
        Assert.Equal(0, calls); Assert.Empty(await host.Services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.UsageReservations));
    }

    [Fact]
    public async System.Threading.Tasks.Task Failed_attempt_is_recorded_and_never_replayed_automatically()
    {
        var calls = 0;
        using var host = new ProviderFactory(_ => { Interlocked.Increment(ref calls); return System.Threading.Tasks.Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)); });
        var service = host.Services.GetRequiredService<ITextReasoningProvider>(); var request = Task();
        await Assert.ThrowsAsync<DomainException>(() => service.ReasonAsync(request, default));
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => service.ReasonAsync(request, default))).StatusCode);
        Assert.Equal(1, calls);
        Assert.Equal("Uncertain", (await host.Services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.ProviderAttempts[request.OperationId])).Outcome);
    }

    [Fact]
    public async System.Threading.Tasks.Task Rate_and_model_snapshot_survives_configuration_change_during_response()
    {
        IConfiguration? config = null;
        using var host = new ProviderFactory(_ => {
            config!["AI:Chat:Model"] = "changed-after-dispatch";
            config["AI:Chat:InputUsdPerMillion"] = "999";
            return System.Threading.Tasks.Task.FromResult(Result("openai"));
        });
        config = host.Services.GetRequiredService<IConfiguration>();
        var result = await host.Services.GetRequiredService<ITextReasoningProvider>().ReasonAsync(Task(), default);
        var charge = Assert.Single(await host.Services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.UsageReservations.Values.ToArray()));
        Assert.Equal("test-model", result.Route.Model); Assert.Equal("test-model", charge.Model);
        Assert.Equal((10 * result.Route.InputUsdPerMillion + 10 * result.Route.OutputUsdPerMillion) / 1_000_000m, charge.ActualUsd);
    }
}
