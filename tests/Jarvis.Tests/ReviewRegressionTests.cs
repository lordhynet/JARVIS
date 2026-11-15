using System.Net;
using System.Text.Json;
using Jarvis.Api.Integrations;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Workflows;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed class ReviewRegressionTests
{
    [Theory]
    [InlineData("http://192.168.0.99:8123", false, false)]
    [InlineData("http://192.168.0.99:8123", true, true)]
    [InlineData("http://127.0.0.1:8123", false, true)]
    [InlineData("https://homeassistant.test:8123", false, true)]
    public async Task Home_tokens_are_not_sent_over_cleartext_lan_without_explicit_authorization(string url, bool optIn, bool allowed)
    {
        var calls = 0;
        using var original = new ProviderFactory(_ => { Interlocked.Increment(ref calls); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") }); });
        using var host = original.WithWebHostBuilder(b => { b.UseSetting("HomeAssistant:BaseUrl", url); b.UseSetting("HomeAssistant:AllowInsecureLan", optIn.ToString()); });
        var gateway = host.Services.GetRequiredService<ProviderGateway>();
        if (allowed) await gateway.ExecuteAsync("light.desk", "turn_on", default);
        else Assert.Equal(503, (await Assert.ThrowsAsync<DomainException>(() => gateway.ExecuteAsync("light.desk", "turn_on", default))).StatusCode);
        Assert.Equal(allowed ? 1 : 0, calls);
    }

    [Fact]
    public async Task Agent_context_selects_newest_stories_and_uses_the_injected_clock_for_due_cards()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException("No paid calls."));
        var clock = new BudgetClock(); var store = new InMemoryRuntimeStore(); var now = clock.GetUtcNow();
        var storyIds = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray(); var due = Guid.NewGuid(); var future = Guid.NewGuid();
        await store.UpdateAsync(s => {
            for (var i = 0; i < 3; i++) s.Stories[storyIds[i]] = new(storyIds[i], new("Story " + i, "Project", "Service"), 1, now.AddDays(i - 3), now.AddDays(i - 3));
            s.Learning[due] = new(due, LearningDomain.French, "Due", "Answer", 0, 5, now.AddHours(-1), now);
            s.Learning[future] = new(future, LearningDomain.French, "Future", "Answer", 0, 5, now.AddHours(1), now);
            return true;
        });
        var runtime = new AgentRuntime(store, clock);
        await runtime.StartAsync(new(Guid.NewGuid(), "daily-coaching", false), default);
        var task = (await runtime.ClaimAsync(default))!;
        var executor = new AgentExecutor(host.Services.GetRequiredService<ManagedAi>(), store, clock);
        using var result = JsonDocument.Parse(await executor.ExecuteAsync(task, default));
        var selected = result.RootElement.GetProperty("storyIds").EnumerateArray().Select(x => x.GetGuid()).ToArray();
        Assert.Equal(new[] { storyIds[2], storyIds[1] }, selected);
        Assert.Equal(due, Assert.Single(result.RootElement.GetProperty("drills").EnumerateArray()).GetGuid());
    }
}
