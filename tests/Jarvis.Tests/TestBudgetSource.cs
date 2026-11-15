using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

// Explicit synthetic test-only prices. Never used by production composition or written into settings.
public sealed class TestBudgetSource(TimeProvider clock) : IBudgetPolicySource
{
    public GbpBudgetPolicy Read() => new(true, "fixture-account", "Development", "fixture-v1",
        new("synthetic-test-only", 1m, clock.GetUtcNow().AddMinutes(-1), clock.GetUtcNow().AddHours(1)), 1m, 1m);
    public static readonly BudgetActor Owner = new("fixture-owner", ["budget.manage"]);
    public static void Configure(IWebHostBuilder builder)
    {
        TestOwnerManagement.Configure(builder);
        foreach (var profile in new[] { "Chat", "Agent", "Research", "Coach", "Training", "Voice", "Transcription" }) builder.UseSetting($"AI:{profile}:RateCardDate", "2026-11-13");
        foreach (var profile in new[] { "Chat", "Agent", "Research", "Coach", "Training" }) builder.UseSetting($"AI:{profile}:RateCardModel", "gpt-4.1-mini");
        builder.UseSetting("AI:Voice:RateCardModel", "gpt-realtime-2.1-mini"); builder.UseSetting("AI:Transcription:RateCardModel", "gpt-transcribe");
        foreach (var rate in new[] { "AudioInput", "TextInput", "CachedAudioInput", "CachedTextInput", "AudioOutput", "TextOutput" }) builder.UseSetting($"AI:Voice:{rate}UsdPerMillion", "1");
        builder.UseSetting("AI:Voice:TranscriptionUsdPerMinute", "0.01"); builder.UseSetting("AI:Voice:TranscriptionModel", "fixture-transcription");
        builder.UseSetting("AI:Voice:TranscriptionRateCardModel", "fixture-transcription");
        builder.UseSetting("AI:Research:SearchUsdPerCall", "0.01");
        builder.ConfigureServices(s => s.AddSingleton<IBudgetPolicySource>(p => new TestBudgetSource(p.GetRequiredService<TimeProvider>())));
    }
}
