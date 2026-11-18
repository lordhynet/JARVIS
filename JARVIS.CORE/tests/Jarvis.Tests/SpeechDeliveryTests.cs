using System.Buffers.Binary;
using System.Net.Http.Json;
using System.Text.Json;
using Jarvis.Api.Voice;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Coaching;
using Jarvis.Orchestrator.Runtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed class SpeechDeliveryTests
{
    [Fact]
    public void Fixed_transcript_and_duration_give_exact_core_metrics_and_unknown_timing_stays_unknown()
    {
        var turn = new VoiceTurn(Guid.NewGuid(), Guid.NewGuid(), ChatRoles.User, "I chose queues. Um, we tested it and I owned recovery.", 30, DateTimeOffset.UtcNow,
            Timing: new(30, 6, SpeechDelivery.PcmQuietMethod));
        var measured = SpeechDelivery.Measure(turn);
        Assert.Equal(11, measured.Words); Assert.Equal(22, measured.WordsPerMinute); Assert.Equal(1, measured.Fillers);
        Assert.Equal(2, measured.FirstPersonSingular); Assert.Equal(1, measured.FirstPersonPlural); Assert.Equal(.667, measured.PersonalPronounShare);
        Assert.Equal(.2, measured.PauseRatio); Assert.Equal(2, measured.OpeningScore);
        var untimed = SpeechDelivery.Measure(turn with { SpeechSeconds = null, Timing = null }); Assert.Null(untimed.WordsPerMinute); Assert.Null(untimed.PauseRatio);
        Assert.Throws<DomainException>(() => SpeechDelivery.Measure(turn with { Restricted = true }));
    }
    private static byte[] Audio(int rate = 24000)
    {
        var pcm = new byte[rate * 2 * 3];
        for (var sample = 0; sample < rate * 3; sample++) if (sample < rate || sample >= rate * 2) BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(sample * 2), 5000);
        return pcm;
    }
    [Theory]
    [InlineData(16000)]
    [InlineData(24000)]
    public void Pcm_quiet_intervals_are_measured_with_fixed_samples_and_bounded_ranges(int rate)
    {
        var timing = SpeechAudioMeter.Analyse(Audio(rate), rate)!; Assert.Equal(3, timing.AudioSeconds); Assert.Equal(1, timing.QuietSeconds);
        Assert.Null(SpeechAudioMeter.Analyse(new byte[rate * 2], rate));
        using var meter = new SpeechAudioMeter(24000 * 6); meter.Append(Audio());
        Assert.Equal(timing, meter.Measure(0, 3000)); Assert.Null(meter.Measure(-1, 3000)); Assert.Null(meter.Measure(0, 4000));
        meter.Dispose(); Assert.Null(meter.Measure(0, 3000)); meter.Append(Audio()); Assert.Null(meter.Measure(0, 3000));
    }
    [Fact]
    public void Invalid_timing_is_refused_and_null_metadata_preserves_legacy_source_bytes()
    {
        var turn = new VoiceTurn(Guid.NewGuid(), Guid.NewGuid(), ChatRoles.User, "Hello", 10, DateTimeOffset.UtcNow);
        Assert.DoesNotContain("Timing", JsonSerializer.Serialize(turn));
        foreach (var timing in new[] { new SpeechTiming(10, 11, SpeechDelivery.PcmQuietMethod), new(10, double.NaN, SpeechDelivery.PcmQuietMethod), new(12, 1, SpeechDelivery.PcmQuietMethod), new(10, 1, "model-guessed") })
            Assert.Throws<DomainException>(() => VoiceTranscriptRules.Validate(turn with { Timing = timing }));
    }
    [Fact]
    public async Task Canonical_timing_survives_transcript_publication_and_dashboard_excludes_locked_assistant_and_retired_turns()
    {
        using var host = new JarvisApiFactory().WithWebHostBuilder(b => b.UseSetting("Jarvis:WorkflowsEnabled", "false"));
        var session = (await host.Services.GetRequiredService<Jarvis.Orchestrator.Sessions.SessionService>().CreateAsync(new(ActivityType.Communication, "Speech fixture", null))).Id;
        await VoiceToolFixture.OpenAsync(host.Services, session);
        var source = Guid.NewGuid(); var transcripts = host.Services.GetRequiredService<VoiceTranscriptService>();
        var timing = new SpeechTiming(3, 1, SpeechDelivery.PcmQuietMethod);
        await transcripts.RecordAsync(session, source, ChatRoles.User, "I chose queues and we tested recovery", 3, default, timing: timing);
        var store = host.Services.GetRequiredService<IRuntimeStore>(); var now = DateTimeOffset.UtcNow;
        await store.UpdateAsync(s => {
            Assert.Equal(timing, s.VoiceTurns[source].Timing);
            var id = Guid.NewGuid(); s.VoiceTurns[id] = new(id, session, ChatRoles.User, "PRIVATE LOCKED WORDS", 1, now, true);
            id = Guid.NewGuid(); s.VoiceTurns[id] = new(id, session, ChatRoles.Assistant, "Assistant words", 1, now);
            var retired = Guid.NewGuid(); s.RetiredSessions.Add(retired); id = Guid.NewGuid(); s.VoiceTurns[id] = new(id, retired, ChatRoles.User, "Retired words", 1, now); return true;
        });
        using var client = host.CreateClient(); client.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey);
        var report = (await client.GetFromJsonAsync<SpeechDeliverySession[]>("/api/coaching/delivery"))!;
        var measured = Assert.Single(report); Assert.Equal(session, measured.SessionId); Assert.Equal(1, measured.Turns); Assert.Equal(.333, measured.PauseRatio);
        Assert.Equal(source, Assert.Single(measured.Trend).TurnId);
        Assert.Equal(timing, (await host.Services.GetRequiredService<Jarvis.Orchestrator.Sessions.ISessionReader>().GetAsync(session))!.Messages!.Single(m => m.RequestId == source).Timing);
    }
    [Fact]
    public void Session_pace_is_duration_weighted_and_trend_is_chronological()
    {
        var state = new RuntimeState(); var session = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        var a = Guid.NewGuid(); var b = Guid.NewGuid();
        state.VoiceTurns[b] = new(b, session, ChatRoles.User, "I chose one", 30, now);
        state.VoiceTurns[a] = new(a, session, ChatRoles.User, "We tested this", 10, now.AddMinutes(-1));
        var measured = Assert.Single(SpeechDelivery.Sessions(state)); Assert.Equal(9, measured.WordsPerMinute);
        Assert.Equal(new[] { a, b }, measured.Trend.Select(t => t.TurnId)); Assert.Null(measured.PauseRatio);
    }
}
