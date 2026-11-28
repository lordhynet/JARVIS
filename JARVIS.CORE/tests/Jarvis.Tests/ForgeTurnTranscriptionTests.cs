using System.Net;
using System.Text;
using Jarvis.Api;
using Jarvis.Api.Integrations;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

// Stubbed HTTP only: no provider is called. The fixture transcription model is neither gpt-transcribe nor a logprob model.
public sealed class ForgeTurnTranscriptionTests
{
    private static readonly byte[] TwoSeconds = new byte[48000 * 2];
    private static Task<bool> Owned(CancellationToken _) => Task.FromResult(true);
    private static HttpResponseMessage Reply(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static (ProviderFactory Root, Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Host) Create(
        Func<HttpRequestMessage, Task<HttpResponseMessage>> handle, string? allowance)
    {
        var root = new ProviderFactory(handle);
        return (root, root.WithWebHostBuilder(b => { if (allowance is not null) b.UseSetting(ConfigKeys.ForgeMonthlyAllowanceGbp, allowance); }));
    }

    [Fact]
    public async Task Without_an_owner_allowance_the_turn_is_refused_before_any_provider_call_or_charge()
    {
        var calls = 0;
        var (root, host) = Create(_ => { calls++; return Task.FromResult(Reply("""{"text":"x"}""")); }, allowance: null);
        using (root) using (host)
        {
            var provider = host.Services.GetRequiredService<OpenAiProvider>();
            var error = await Assert.ThrowsAsync<DomainException>(() =>
                provider.TranscribeForgeTurnAsync(TwoSeconds, Guid.NewGuid(), Guid.NewGuid(), "fr-FR", Owned, default));
            Assert.Equal(503, error.StatusCode); Assert.Equal(0, calls);
            Assert.Empty(await host.Services.GetRequiredService<IRuntimeReader>().ReadAsync(s => s.UsageReservations.Values.ToList()));
        }
    }

    [Fact]
    public async Task Turn_is_Forge_attributed_sends_the_profile_language_and_never_admits_unreviewed_evidence()
    {
        string? body = null;
        var (root, host) = Create(async request =>
        {
            Assert.EndsWith("/audio/transcriptions", request.RequestUri!.AbsoluteUri);
            body = await request.Content!.ReadAsStringAsync();
            return Reply("""{"text":"Je voudrais une table","languages":[{"code":"fr"}],"logprobs":[{"logprob":-0.01}]}""");
        }, allowance: "5");
        using (root) using (host)
        {
            var provider = host.Services.GetRequiredService<OpenAiProvider>();
            var turn = Guid.NewGuid(); var session = Guid.NewGuid();
            var transcript = await provider.TranscribeForgeTurnAsync(TwoSeconds, session, turn, "fr-FR", Owned, default);
            Assert.Equal("Je voudrais une table", transcript.Text); Assert.Equal("fr", transcript.DetectedLanguage);
            Assert.Null(transcript.Confidence); Assert.False(transcript.QualityEvidenceAvailable);
            Assert.Contains("name=language", body); Assert.Contains("RIFF", body);
            Assert.DoesNotContain("logprobs", body); Assert.DoesNotContain("languages[]", body);
            var charge = await host.Services.GetRequiredService<IRuntimeReader>().ReadAsync(s => s.UsageReservations[turn]);
            Assert.Equal(UsageProgrammes.Forge, charge.Programme); Assert.Equal(UsageLane.Voice, charge.Lane);
            Assert.Equal(session, charge.RunId); Assert.Equal(ProviderIds.OpenAi, charge.Provider);
        }
    }

    [Fact]
    public async Task A_replayed_turn_is_refused_by_the_budget_and_never_dispatched_or_billed_twice()
    {
        var calls = 0;
        var (root, host) = Create(_ => { calls++; return Task.FromResult(Reply("""{"text":"Bonjour"}""")); }, allowance: "5");
        using (root) using (host)
        {
            var provider = host.Services.GetRequiredService<OpenAiProvider>();
            var turn = Guid.NewGuid(); var session = Guid.NewGuid();
            await provider.TranscribeForgeTurnAsync(TwoSeconds, session, turn, "fr-FR", Owned, default);
            var replay = await Assert.ThrowsAsync<DomainException>(() => provider.TranscribeForgeTurnAsync(TwoSeconds, session, turn, "fr-FR", Owned, default));
            Assert.Equal(409, replay.StatusCode); Assert.Equal(1, calls);
            Assert.Single(await host.Services.GetRequiredService<IRuntimeReader>().ReadAsync(s => s.UsageReservations.Values.ToList()));
        }
    }

    public static TheoryData<byte[], string, bool> InvalidInput => new()
    {
        { TwoSeconds, "fr", false },
        { TwoSeconds, "it-IT", false },
        { Array.Empty<byte>(), "fr-FR", false },
        { new byte[3], "fr-FR", false },
        { new byte[48000 * 181], "fr-FR", false },
        { TwoSeconds, "fr-FR", true }
    };

    [Theory]
    [MemberData(nameof(InvalidInput))]
    public async Task Invalid_turn_input_is_rejected_before_any_charge(byte[] pcm, string locale, bool emptyIds)
    {
        var calls = 0;
        var (root, host) = Create(_ => { calls++; return Task.FromResult(Reply("""{"text":"x"}""")); }, allowance: "5");
        using (root) using (host)
        {
            var provider = host.Services.GetRequiredService<OpenAiProvider>();
            var error = await Assert.ThrowsAsync<DomainException>(() => provider.TranscribeForgeTurnAsync(pcm,
                emptyIds ? Guid.Empty : Guid.NewGuid(), Guid.NewGuid(), locale, Owned, default));
            Assert.Equal(400, error.StatusCode); Assert.Equal(0, calls);
            Assert.Empty(await host.Services.GetRequiredService<IRuntimeReader>().ReadAsync(s => s.UsageReservations.Values.ToList()));
        }
    }

    [Fact]
    public async Task Provider_failure_keeps_the_conservative_Forge_charge_and_does_not_refund()
    {
        var (root, host) = Create(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("{}") }), allowance: "5");
        using (root) using (host)
        {
            var provider = host.Services.GetRequiredService<OpenAiProvider>();
            var turn = Guid.NewGuid();
            await Assert.ThrowsAnyAsync<Exception>(() => provider.TranscribeForgeTurnAsync(TwoSeconds, Guid.NewGuid(), turn, "es-ES", Owned, default));
            var charge = await host.Services.GetRequiredService<IRuntimeReader>().ReadAsync(s => s.UsageReservations[turn]);
            Assert.Null(charge.ActualUsd); Assert.Equal(UsageProgrammes.Forge, charge.Programme);
        }
    }

    // ---- Review W1: current-audio and pause fence after reservation, before dispatch (request token never cancelled) ----

    public static TheoryData<string> FenceFailures => new() { "ownership-lost", "paused-during-reservation", "fence-throws", "fence-cancelled" };

    [Theory]
    [MemberData(nameof(FenceFailures))]
    public async Task A_fence_failure_after_reservation_sends_nothing_and_releases_only_that_charge(string failure)
    {
        var calls = 0;
        var (root, host) = Create(_ => { calls++; return Task.FromResult(Reply("""{"text":"Bonjour"}""")); }, allowance: "5");
        using (root) using (host)
        {
            var provider = host.Services.GetRequiredService<OpenAiProvider>();
            var store = host.Services.GetRequiredService<IRuntimeStore>();
            var turn = Guid.NewGuid();
            async Task<bool> Fence(CancellationToken ct)
            {
                // The reservation has committed by the time the fence runs; prove it, then change state.
                Assert.NotNull(await store.ReadAsync(s => s.UsageReservations.GetValueOrDefault(turn), ct));
                switch (failure)
                {
                    case "ownership-lost": return false;
                    case "paused-during-reservation":
                        await store.UpdateAsync(s => { s.AgentControls = s.AgentControls with { PaidAiPaused = true }; return true; }, ct);
                        return true; // ownership is fine; the method's own pause check must still refuse
                    case "fence-throws": throw new InvalidOperationException("lease lookup failed");
                    default: throw new OperationCanceledException();
                }
            }
            var error = await Assert.ThrowsAnyAsync<Exception>(() => provider.TranscribeForgeTurnAsync(TwoSeconds, Guid.NewGuid(), turn, "fr-FR", Fence, default));
            Assert.Equal(0, calls);
            if (failure == "ownership-lost") Assert.Equal(409, Assert.IsType<DomainException>(error).StatusCode);
            if (failure == "paused-during-reservation") Assert.Equal(503, Assert.IsType<DomainException>(error).StatusCode);
            var charge = await store.ReadAsync(s => s.UsageReservations[turn]);
            Assert.Equal(0m, charge.ActualUsd); Assert.Equal(UsageProgrammes.Forge, charge.Programme);
            Assert.Equal(0m, await store.ReadAsync(s => s.GbpCommitments[turn].AccountedGbp));
            // The turn ID stays used: the same captured audio cannot be re-dispatched under it (un-pause first so the
            // duplicate-ID refusal, not the pause, is what is observed).
            await store.UpdateAsync(s => { s.AgentControls = s.AgentControls with { PaidAiPaused = false }; return true; });
            var replay = await Assert.ThrowsAsync<DomainException>(() => provider.TranscribeForgeTurnAsync(TwoSeconds, Guid.NewGuid(), turn, "fr-FR", Owned, default));
            Assert.Equal(409, replay.StatusCode); Assert.Equal(0, calls);
        }
    }

    [Fact]
    public async Task The_fence_is_required_and_has_no_permissive_default()
    {
        var (root, host) = Create(_ => Task.FromResult(Reply("""{"text":"x"}""")), allowance: "5");
        using (root) using (host)
            await Assert.ThrowsAsync<ArgumentNullException>(() => host.Services.GetRequiredService<OpenAiProvider>()
                .TranscribeForgeTurnAsync(TwoSeconds, Guid.NewGuid(), Guid.NewGuid(), "fr-FR", null!, default));
    }
}

public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task Forge_dispatch_pause_read_uses_the_bounded_controls_read_with_legacy_fallback()
    {
        await using var database = await TestDatabase.CreateAsync(); var clock = new BudgetClock();
        using var host = new PostgresApiFactory(database.ConnectionString);
        var store = host.Services.GetRequiredService<IRuntimeStore>();
        var budget = new Jarvis.Orchestrator.Workflows.UsageBudget(store, clock, new TestBudgetSource(clock));
        Assert.False(await budget.PaidAiPausedAsync(default));
        await store.UpdateAsync(s => { s.AgentControls = s.AgentControls with { PaidAiPaused = true }; return true; });
        Assert.True(await budget.PaidAiPausedAsync(default));
        // Null PaidAiPaused keeps the historical meaning of the legacy Paused flag.
        await store.UpdateAsync(s => { s.AgentControls = s.AgentControls with { PaidAiPaused = null, Paused = true }; return true; });
        Assert.True(await budget.PaidAiPausedAsync(default));
        await store.UpdateAsync(s => { s.AgentControls = s.AgentControls with { PaidAiPaused = false, Paused = true }; return true; });
        Assert.False(await budget.PaidAiPausedAsync(default));
    }
}
