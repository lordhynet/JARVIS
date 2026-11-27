using System.Text.Json;
using Jarvis.Api;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

// PF-06: Forge spend is attributed on the existing reservation and capped by a sub-allowance inside the one global
// GBP ceiling. Fixture policy converts 1 USD to 1 GBP with unit multipliers, so amounts read as pounds.
public sealed class ForgeCostAllowanceTests
{
    private static BudgetLimits Limits(decimal? forge) => new(10000, 10000, 10000, 10000, 10000, ForgeMonthlyGbp: forge);

    private static (InMemoryRuntimeStore Store, UsageBudget Budget, BudgetClock Clock) Create(string environment = "Production")
    {
        var clock = new BudgetClock(); var store = new InMemoryRuntimeStore();
        return (store, new UsageBudget(store, clock, new MutableBudgetSource(MutableBudgetSource.Fixture(clock.Now, environment))), clock);
    }

    private static Task<UsageReservation> Forge(UsageBudget budget, decimal amount, decimal? allowance) =>
        budget.ReserveAsync(Guid.NewGuid(), Guid.NewGuid(), "fixture", amount, Limits(allowance), default, UsageLane.Interactive,
            ProviderIds.OpenAi, UsageProgrammes.Forge);

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(200.01d)]
    public async Task Forge_work_is_refused_without_a_valid_owner_allowance_and_writes_nothing(double? allowance)
    {
        var (store, budget, _) = Create();
        var error = await Assert.ThrowsAsync<DomainException>(() => Forge(budget, 1m, (decimal?)allowance));
        Assert.Equal(503, error.StatusCode);
        Assert.Empty(await store.ReadAsync(s => s.UsageReservations));
        Assert.Empty(await store.ReadAsync(s => s.GbpCommitments));
    }

    [Fact]
    public async Task Allowance_caps_only_Forge_attributed_spend_and_ordinary_work_continues()
    {
        var (store, budget, _) = Create();
        await Forge(budget, 3m, 5m); await Forge(budget, 2m, 5m);
        var error = await Assert.ThrowsAsync<DomainException>(() => Forge(budget, .01m, 5m));
        Assert.Equal(429, error.StatusCode); Assert.Equal(UsageProgrammes.ForgeAllowanceReached, error.Code);
        // Unattributed work is governed only by the existing global and lane limits.
        await budget.ReserveAsync(Guid.NewGuid(), Guid.NewGuid(), "fixture", 20m, Limits(5m), default);
        var charges = await store.ReadAsync(s => s.UsageReservations.Values.ToArray());
        Assert.Equal(2, charges.Count(c => c.Programme == UsageProgrammes.Forge));
        Assert.Single(charges, c => c.Programme is null);
    }

    [Fact]
    public async Task Ordinary_spend_does_not_consume_the_Forge_allowance_but_still_counts_globally()
    {
        var (_, budget, _) = Create("Development");
        await budget.ReserveAsync(Guid.NewGuid(), Guid.NewGuid(), "fixture", 8m, Limits(5m), default);
        await Forge(budget, 2m, 5m);
        var error = await Assert.ThrowsAsync<DomainException>(() => Forge(budget, .01m, 5m));
        Assert.Equal(VoiceEndReasons.GbpBudgetCeiling, error.Code);
    }

    [Fact]
    public async Task A_larger_allowance_can_never_raise_the_global_ceiling()
    {
        var (store, budget, _) = Create("Development");
        await Forge(budget, 10m, 200m);
        var error = await Assert.ThrowsAsync<DomainException>(() => Forge(budget, .01m, 200m));
        Assert.Equal(429, error.StatusCode); Assert.Equal(VoiceEndReasons.GbpBudgetCeiling, error.Code);
        Assert.Single(await store.ReadAsync(s => s.UsageReservations));
    }

    [Fact]
    public async Task Settlement_keeps_attribution_and_frees_unused_allowance_while_unsettled_charges_hold_their_maximum()
    {
        var (store, budget, _) = Create();
        var settled = await Forge(budget, 4m, 5m);
        await Forge(budget, 1m, 5m);
        await Assert.ThrowsAsync<DomainException>(() => Forge(budget, .01m, 5m));
        await budget.SettleAsync(settled.Id, 1m, 10, 10, default);
        Assert.Equal(UsageProgrammes.Forge, await store.ReadAsync(s => s.UsageReservations[settled.Id].Programme));
        await Forge(budget, 3m, 5m);
        await Assert.ThrowsAsync<DomainException>(() => Forge(budget, .01m, 5m));
    }

    [Fact]
    public async Task Allowance_resets_with_the_GBP_accounting_month()
    {
        var (store, budget, clock) = Create();
        await Forge(budget, 5m, 5m);
        await Assert.ThrowsAsync<DomainException>(() => Forge(budget, .01m, 5m));
        clock.Now = clock.Now.AddMonths(1);
        // Same store in the next month, with a quote that is current for the new clock.
        var nextMonth = new UsageBudget(store, clock, new MutableBudgetSource(MutableBudgetSource.Fixture(clock.Now)));
        await Forge(nextMonth, 5m, 5m);
        await Assert.ThrowsAsync<DomainException>(() => Forge(nextMonth, .01m, 5m));
    }

    [Fact]
    public async Task Unknown_attribution_is_rejected_and_a_replayed_id_is_still_refused()
    {
        var (store, budget, _) = Create();
        var unknown = await Assert.ThrowsAsync<DomainException>(() => budget.ReserveAsync(Guid.NewGuid(), Guid.NewGuid(), "fixture", 1m,
            Limits(5m), default, programme: "Forge"));
        Assert.Equal(400, unknown.StatusCode);
        var id = Guid.NewGuid();
        await budget.ReserveAsync(id, Guid.NewGuid(), "fixture", 1m, Limits(5m), default, programme: UsageProgrammes.Forge);
        var replay = await Assert.ThrowsAsync<DomainException>(() => budget.ReserveAsync(id, Guid.NewGuid(), "fixture", 1m,
            Limits(5m), default, programme: UsageProgrammes.Forge));
        Assert.Equal(409, replay.StatusCode);
        Assert.Single(await store.ReadAsync(s => s.UsageReservations));
    }

    [Fact]
    public async Task Forge_voice_reservations_carry_attribution_on_the_existing_attempt_path()
    {
        var (store, budget, clock) = Create();
        var route = new ModelRoute(ProviderIds.OpenAi, "fixture-model", "Voice:Everyday", "fixture-policy",
            "fixture-rate-card", DateOnly.FromDateTime(clock.Now.UtcDateTime), 1, 1, 256);
        var charge = await budget.ReserveAsync(Guid.NewGuid(), Guid.NewGuid(), route, VoiceMode.Everyday, 2m, Limits(2m),
            default, programme: UsageProgrammes.Forge);
        Assert.Equal(UsageProgrammes.Forge, charge.Programme);
        Assert.True(await store.ReadAsync(s => s.ProviderAttempts.ContainsKey(charge.Id)));
        var error = await Assert.ThrowsAsync<DomainException>(() => budget.ReserveAsync(Guid.NewGuid(), Guid.NewGuid(), route,
            VoiceMode.Everyday, .01m, Limits(2m), default, programme: UsageProgrammes.Forge));
        Assert.Equal(UsageProgrammes.ForgeAllowanceReached, error.Code);
    }

    [Fact]
    public async Task Concurrent_Forge_reservations_never_exceed_the_allowance()
    {
        var (store, budget, _) = Create();
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(async _ =>
        {
            try { await Forge(budget, 1m, 5m); return true; }
            catch (DomainException e) when (e.StatusCode == 429) { return false; }
        }));
        Assert.Equal(5, results.Count(ok => ok));
        Assert.Equal(5, await store.ReadAsync(s => s.UsageReservations.Count));
    }

    [Fact]
    public void Unattributed_reservations_serialize_exactly_as_before()
    {
        var at = DateTimeOffset.Parse("2026-11-14T10:00:00Z");
        var plain = new UsageReservation(Guid.Empty, Guid.Empty, "fixture", at, 1m);
        foreach (var options in new[] { new JsonSerializerOptions(), new JsonSerializerOptions(JsonSerializerDefaults.Web) })
        {
            var json = JsonSerializer.Serialize(plain, options);
            Assert.DoesNotContain("rogramme", json);
            var attributed = JsonSerializer.Serialize(plain with { Programme = UsageProgrammes.Forge }, options);
            Assert.Equal(UsageProgrammes.Forge, JsonSerializer.Deserialize<UsageReservation>(attributed, options)!.Programme);
            Assert.Null(JsonSerializer.Deserialize<UsageReservation>(json, options)!.Programme);
        }
    }

    [Theory]
    [InlineData("12.50", 12.50)]
    [InlineData("abc", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Allowance_configuration_is_parsed_invariantly_and_malformed_values_mean_no_paid_Forge_work(string? value, double? expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ConfigKeys.ForgeMonthlyAllowanceGbp] = value
        }).Build();
        Assert.Equal((decimal?)expected, ForgeSettings.MonthlyAllowanceGbp(config));
    }
}

public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task Forge_allowance_is_atomic_across_hosts_and_survives_restart()
    {
        await using var database = await TestDatabase.CreateAsync(); var clock = new BudgetClock();
        var source = new MutableBudgetSource(MutableBudgetSource.Fixture(clock.Now, "Development"));
        using var a = new PostgresApiFactory(database.ConnectionString); using var b = new PostgresApiFactory(database.ConnectionString);
        using var left = a.WithWebHostBuilder(w => w.ConfigureServices(s => { s.AddSingleton<IBudgetPolicySource>(source); s.AddSingleton<TimeProvider>(clock); }));
        using var right = b.WithWebHostBuilder(w => w.ConfigureServices(s => { s.AddSingleton<IBudgetPolicySource>(source); s.AddSingleton<TimeProvider>(clock); }));
        using var x = left.CreateClient(); using var y = right.CreateClient();
        var l = left.Services.GetRequiredService<UsageBudget>(); var r = right.Services.GetRequiredService<UsageBudget>();
        var limits = new BudgetLimits(1000, 1000, 1000, 1000, 1000, ForgeMonthlyGbp: 3m);
        var attempts = await Task.WhenAll(Enumerable.Range(0, 16).Select(async i =>
        {
            try
            {
                await (i % 2 == 0 ? l : r).ReserveAsync(Guid.NewGuid(), Guid.NewGuid(), "fixture", .5m, limits, default,
                    UsageLane.Interactive, ProviderIds.OpenAi, UsageProgrammes.Forge);
                return true;
            }
            catch (DomainException e) when (e.StatusCode == 429) { return false; }
        }));
        Assert.Equal(6, attempts.Count(ok => ok));
        using var restarted = new PostgresApiFactory(database.ConnectionString);
        using var restored = restarted.WithWebHostBuilder(w => w.ConfigureServices(s => { s.AddSingleton<IBudgetPolicySource>(source); s.AddSingleton<TimeProvider>(clock); }));
        using var z = restored.CreateClient();
        var charges = await restored.Services.GetRequiredService<IRuntimeReader>().ReadAsync(s => s.UsageReservations.Values.ToArray());
        Assert.Equal(6, charges.Length); Assert.All(charges, c => Assert.Equal(UsageProgrammes.Forge, c.Programme));
        var denied = await Assert.ThrowsAsync<DomainException>(() => restored.Services.GetRequiredService<UsageBudget>()
            .ReserveAsync(Guid.NewGuid(), Guid.NewGuid(), "fixture", .01m, limits, default, programme: UsageProgrammes.Forge));
        Assert.Equal(UsageProgrammes.ForgeAllowanceReached, denied.Code);
    }
}
