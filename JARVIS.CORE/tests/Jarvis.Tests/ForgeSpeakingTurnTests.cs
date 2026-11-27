using System.Buffers.Binary;
using System.Net;
using System.Text;
using Jarvis.Api;
using Jarvis.Api.Integrations;
using Jarvis.Api.Voice;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using R = Jarvis.Orchestrator.Forge.ForgeTurnUnscorableReason;

namespace Jarvis.Tests;

// Synthetic 24 kHz PCM through the real meter; stubbed HTTP only. Tones are not speech and prove no acoustic quality.
public sealed class ForgeSpeakingTurnTests
{
    private const int Rate = 24000;
    private static Task<bool> Owned(CancellationToken _) => Task.FromResult(true);
    private static HttpResponseMessage Reply(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static byte[] Pcm(double seconds, double toneStart = 0, double toneLength = 0)
    {
        var bytes = new byte[(int)Math.Round(Rate * seconds) * 2];
        for (var i = (int)Math.Round(toneStart * Rate); i < (int)Math.Round((toneStart + toneLength) * Rate); i++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), (short)(6000 * Math.Sin(2 * Math.PI * 220 * i / Rate)));
        return bytes;
    }

    private sealed class Harness : IDisposable
    {
        private readonly ProviderFactory root;
        public readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> Host;
        public int Calls;
        public Harness(string reply, string? allowance = "5")
        {
            root = new ProviderFactory(_ => { Interlocked.Increment(ref Calls); return Task.FromResult(Reply(reply)); });
            Host = root.WithWebHostBuilder(b => { if (allowance is not null) b.UseSetting(ConfigKeys.ForgeMonthlyAllowanceGbp, allowance); });
        }
        public ForgeSpeakingTurn Turn => new(Host.Services.GetRequiredService<OpenAiProvider>());
        public Task<List<UsageReservation>> Charges() =>
            Host.Services.GetRequiredService<IRuntimeReader>().ReadAsync(s => s.UsageReservations.Values.ToList());
        public void Dispose() { Host.Dispose(); root.Dispose(); }
    }

    [Theory]
    [InlineData(2, 0, 0, R.NoMeasuredSpeech)]       // silence
    [InlineData(3, 1, .3, R.TooShort)]              // a short sound padded with silence
    public async Task Audio_that_cannot_qualify_is_refused_with_a_retry_and_costs_nothing(double seconds, double start, double length, R expected)
    {
        using var h = new Harness("""{"text":"Bonjour"}""");
        var result = await h.Turn.RunAsync(Pcm(seconds, start, length), Guid.NewGuid(), Guid.NewGuid(), "fr-FR", Owned, default);
        Assert.Equal(expected, result.Quality.Reason); Assert.True(result.Quality.RetryOffered);
        Assert.False(result.ProviderCalled); Assert.Null(result.Transcript);
        Assert.Equal(0, h.Calls); Assert.Empty(await h.Charges());
    }

    [Fact]
    public async Task Clean_turn_on_a_model_without_quality_evidence_is_practice_only_and_Forge_attributed()
    {
        using var h = new Harness("""{"text":"Je voudrais une table pour deux","languages":[{"code":"fr"}]}""");
        var turn = Guid.NewGuid();
        var result = await h.Turn.RunAsync(Pcm(4, .5, 3), Guid.NewGuid(), turn, "fr-FR", Owned, default);
        Assert.True(result.ProviderCalled); Assert.Equal(1, h.Calls);
        Assert.True(result.Quality.PracticeOnly); Assert.False(result.Quality.Scorable); Assert.False(result.Quality.RetryOffered);
        Assert.Equal("Je voudrais une table pour deux", result.Transcript!.Text);
        Assert.Equal(UsageProgrammes.Forge, Assert.Single(await h.Charges()).Programme);
    }

    [Fact]
    public async Task Speaking_the_wrong_language_asks_for_a_retry()
    {
        using var h = new Harness("""{"text":"I would like a table for two","languages":[{"code":"en"}]}""");
        var result = await h.Turn.RunAsync(Pcm(4, .5, 3), Guid.NewGuid(), Guid.NewGuid(), "fr-FR", Owned, default);
        Assert.Equal(R.LanguageMismatch, result.Quality.Reason); Assert.True(result.Quality.RetryOffered);
    }

    [Fact]
    public async Task A_late_transcript_after_ownership_changed_is_discarded_and_the_dispatched_charge_is_kept()
    {
        using var h = new Harness("""{"text":"Bonjour à tous"}""");
        var checks = 0;
        Task<bool> OwnedOnlyBeforeDispatch(CancellationToken _) => Task.FromResult(Interlocked.Increment(ref checks) == 1);
        var turn = Guid.NewGuid();
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            h.Turn.RunAsync(Pcm(4, .5, 3), Guid.NewGuid(), turn, "fr-FR", OwnedOnlyBeforeDispatch, default));
        Assert.Equal(409, error.StatusCode); Assert.Equal(1, h.Calls); Assert.Equal(2, checks);
        // Dispatch happened, so conservative accounting stands: no refund.
        Assert.Null(Assert.Single(await h.Charges()).ActualUsd);
    }

    [Fact]
    public async Task Without_an_owner_allowance_a_speaking_turn_is_refused_before_any_provider_call()
    {
        using var h = new Harness("""{"text":"Bonjour"}""", allowance: null);
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            h.Turn.RunAsync(Pcm(4, .5, 3), Guid.NewGuid(), Guid.NewGuid(), "fr-FR", Owned, default));
        Assert.Equal(503, error.StatusCode); Assert.Equal(0, h.Calls); Assert.Empty(await h.Charges());
    }

    [Theory]
    [InlineData("fr")]
    [InlineData("it-IT")]
    public async Task An_unsupported_locale_is_rejected_before_measuring_or_charging(string locale)
    {
        using var h = new Harness("""{"text":"Bonjour"}""");
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            h.Turn.RunAsync(Pcm(4, .5, 3), Guid.NewGuid(), Guid.NewGuid(), locale, Owned, default));
        Assert.Equal(400, error.StatusCode); Assert.Equal(0, h.Calls); Assert.Empty(await h.Charges());
    }

    [Fact]
    public async Task The_fence_is_required()
    {
        using var h = new Harness("""{"text":"Bonjour"}""");
        await Assert.ThrowsAsync<ArgumentNullException>(() => h.Turn.RunAsync(Pcm(4, .5, 3), Guid.NewGuid(), Guid.NewGuid(), "fr-FR", null!, default));
    }
}
