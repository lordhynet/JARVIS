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
                provider.TranscribeForgeTurnAsync(TwoSeconds, Guid.NewGuid(), Guid.NewGuid(), "fr-FR", default));
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
            var transcript = await provider.TranscribeForgeTurnAsync(TwoSeconds, session, turn, "fr-FR", default);
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
            await provider.TranscribeForgeTurnAsync(TwoSeconds, session, turn, "fr-FR", default);
            var replay = await Assert.ThrowsAsync<DomainException>(() => provider.TranscribeForgeTurnAsync(TwoSeconds, session, turn, "fr-FR", default));
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
                emptyIds ? Guid.Empty : Guid.NewGuid(), Guid.NewGuid(), locale, default));
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
            await Assert.ThrowsAnyAsync<Exception>(() => provider.TranscribeForgeTurnAsync(TwoSeconds, Guid.NewGuid(), turn, "es-ES", default));
            var charge = await host.Services.GetRequiredService<IRuntimeReader>().ReadAsync(s => s.UsageReservations[turn]);
            Assert.Null(charge.ActualUsd); Assert.Equal(UsageProgrammes.Forge, charge.Programme);
        }
    }
}
