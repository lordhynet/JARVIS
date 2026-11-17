using System.Net.Http.Json;
using System.Text.Json;
using Jarvis.Api;
using Jarvis.Api.Integrations;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Intelligence;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;
using Microsoft.Extensions.Configuration;

namespace Jarvis.Tests;

public sealed class VoiceModePolicyTests
{
    [Theory]
    [InlineData(ActivityType.General, null, VoiceMode.Everyday)]
    [InlineData(ActivityType.French, null, VoiceMode.Everyday)]
    [InlineData(ActivityType.Communication, null, VoiceMode.Serious)]
    [InlineData(ActivityType.MockInterview, null, VoiceMode.Serious)]
    [InlineData(ActivityType.SystemDesign, null, VoiceMode.Serious)]
    [InlineData(ActivityType.SystemDesign, TrainingMode.RapidDrill, VoiceMode.Everyday)]
    [InlineData(ActivityType.General, TrainingMode.SpacedReview, VoiceMode.Everyday)]
    [InlineData(ActivityType.LeetCode, TrainingMode.DeepDive, VoiceMode.Serious)]
    [InlineData(ActivityType.General, TrainingMode.RandomMixedInterview, VoiceMode.Serious)]
    public void The_activity_or_active_drill_recommends_a_mode(ActivityType activity, TrainingMode? drill, VoiceMode expected) =>
        Assert.Equal(expected, VoiceModePolicy.Recommend(activity, drill));

    [Fact]
    public void Rate_cards_keep_the_legacy_everyday_route_and_require_a_reviewed_full_model_for_serious()
    {
        var clock = new BudgetClock();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        configuration[ConfigKeys.OpenAiApiKey] = "fixture-key-not-live";
        void Set(string key, string value) => configuration[key] = value;
        Seed(Set, ConfigKeys.VoiceProfile, ConfigKeys.EverydayRealtimeModel);
        var reader = new RateCard(configuration, clock);
        Assert.Equal(ConfigKeys.EverydayRealtimeModel, reader.ReadRealtime().Model);
        var disabled = Assert.Throws<DomainException>(() => reader.ReadRealtime(VoiceMode.Serious));
        Assert.Equal(503, disabled.StatusCode);
        Assert.Contains("Serious voice is disabled", disabled.Message);

        var prefix = ConfigKeys.VoiceModes + VoiceMode.Serious + ":";
        configuration[prefix + ConfigKeys.Enabled] = "true";
        Seed(Set, prefix, ConfigKeys.EverydayRealtimeModel);
        Assert.Throws<DomainException>(() => reader.ReadRealtime(VoiceMode.Serious));
        Seed(Set, prefix, ConfigKeys.SeriousRealtimeModel);
        var serious = reader.ReadRealtime(VoiceMode.Serious);
        Assert.Equal(ConfigKeys.SeriousRealtimeModel, serious.Route.Model);
        Assert.NotEqual(reader.ReadRealtime(VoiceMode.Everyday).Route.RateCardRevision, serious.Route.RateCardRevision);
        configuration[prefix + "CachedAudioInputUsdPerMillion"] = "0";
        Assert.Throws<DomainException>(() => reader.ReadRealtime(VoiceMode.Serious));
    }

    [Fact]
    public async Task Estimates_are_owner_visible_without_reserving_a_provider_call()
    {
        using var root = new ProviderFactory(_ => throw new InvalidOperationException("Provider traffic is not expected."));
        using var host = root.WithWebHostBuilder(builder =>
        {
            var prefix = ConfigKeys.VoiceModes + VoiceMode.Serious + ":";
            builder.UseSetting(prefix + ConfigKeys.Enabled, "true");
            builder.UseSetting(ConfigKeys.VoiceSeriousDailyGbp, "1.00");
            Seed((key, value) => builder.UseSetting(key, value), prefix, ConfigKeys.SeriousRealtimeModel);
        });
        using var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey);
        var options = await client.GetFromJsonAsync<JsonElement>("/api/voice/modes?activity=SystemDesign");
        Assert.Equal("Serious", options.GetProperty("recommended").GetString());
        var serious = options.GetProperty("modes").EnumerateArray().Single(mode => mode.GetProperty("mode").ValueEquals("Serious"));
        Assert.True(serious.GetProperty("pricingReady").GetBoolean());
        Assert.Equal(1m, serious.GetProperty("dailyCapGbp").GetDecimal());
        Assert.Equal(ConfigKeys.SeriousRealtimeModel, serious.GetProperty("model").GetString());
        Assert.True(serious.GetProperty("maximumGbpForTenMinutes").GetDecimal() > 0);
        var store = host.Services.GetService(typeof(IRuntimeStore)) as IRuntimeStore;
        Assert.Empty(await store!.ReadAsync(state => state.UsageReservations.Values.ToArray()));
    }

    [Fact]
    public async Task Serious_cap_counts_existing_gbp_snapshots_and_leaves_everyday_capacity_available()
    {
        var clock = new BudgetClock();
        var policy = new MutableBudgetSource(MutableBudgetSource.Fixture(clock.Now, "Development"));
        var store = new InMemoryRuntimeStore();
        var ledger = new UsageBudget(store, clock, policy);
        var limits = new BudgetLimits(10, 50, 5, VoiceSeriousDailyGbp: 1);
        var session = Guid.NewGuid();
        var route = new ModelRoute(ProviderIds.OpenAi, ConfigKeys.SeriousRealtimeModel, "Voice:Serious",
            VoiceModePolicy.Version, "fixture", DateOnly.FromDateTime(clock.Now.UtcDateTime), 1, 2, 256);
        var first = await ledger.ReserveAsync(Guid.NewGuid(), session, route, VoiceMode.Serious, .60m, limits, default);
        await ledger.MarkVoiceDispatchedAsync(first.Id, default);
        policy.Policy = policy.Policy with { Fx = policy.Policy.Fx! with { UsdToGbp = 2m } };
        var second = await ledger.ReserveAsync(Guid.NewGuid(), session, route, VoiceMode.Serious, .20m, limits, default);
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            ledger.ReserveAsync(Guid.NewGuid(), session, route, VoiceMode.Serious, .01m, limits, default));
        Assert.Equal(429, error.StatusCode);
        Assert.Contains("Serious voice daily GBP cap reached", error.Message);
        var everyday = await ledger.ReserveAsync(Guid.NewGuid(), session,
            route with { Model = ConfigKeys.EverydayRealtimeModel, Profile = "Voice:Everyday" }, VoiceMode.Everyday, .30m, limits, default);
        Assert.Equal(VoiceMode.Everyday, everyday.VoiceMode);
        await ledger.SettleAsync(first.Id, .05m, 10, 10, default);
        await ledger.SettleAsync(second.Id, 0m, 0, 0, default);
        var status = await new BudgetApprovalService(store, policy, clock).StatusAsync();
        Assert.Equal(3, status.VoiceUsage!.Count);
        Assert.Contains(status.VoiceUsage, x => x is { Mode: VoiceMode.Serious, AccountedGbp: .05m, Outcome: ProviderAttemptOutcome.Completed });
        Assert.Contains(status.VoiceUsage, x => x is { Mode: VoiceMode.Serious, AccountedGbp: 0m, Outcome: ProviderAttemptOutcome.Refused });
        Assert.Contains(status.VoiceUsage, x => x is { Mode: VoiceMode.Everyday, Model: ConfigKeys.EverydayRealtimeModel });
    }

    private static void Seed(Action<string,string> set, string prefix, string model)
    {
        set(prefix + ConfigKeys.Model, model);
        set(prefix + ConfigKeys.RateCardModel, model);
        set(prefix + ConfigKeys.RateCardDate, "2026-11-13");
        foreach (var key in new[] { "AudioInput", "TextInput", "CachedAudioInput", "CachedTextInput", "AudioOutput", "TextOutput" })
            set(prefix + key + "UsdPerMillion", "1");
        set(prefix + "TranscriptionUsdPerMinute", ".01");
        set(prefix + "TranscriptionModel", "fixture-transcription");
        set(prefix + "TranscriptionRateCardModel", "fixture-transcription");
    }
}
