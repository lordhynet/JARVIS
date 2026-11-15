using System.Text.Json;
using Jarvis.Api.Voice;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Intelligence;
using Jarvis.Orchestrator.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed class ContextSelectorTests
{
    private static Story Story(string title, DateTimeOffset at, string ownership = "I tested recovery") =>
        new(Guid.NewGuid(), new(title, "Fixture", "Service", PersonalOwnership: ownership), 1, at, at);

    [Fact]
    public void Explicit_topic_finds_older_story_and_selection_is_stable_and_bounded()
    {
        var now = DateTimeOffset.UtcNow;
        var fx = Story("FX settlement recovery", now.AddYears(-1));
        var stories = Enumerable.Range(0, 12).Select(i => Story("Unrelated release " + i, now.AddDays(-i))).Append(fx).ToArray();
        var first = ContextSelector.Select(stories, [], "Practise my FX story", now, maxBytes: 2000);
        var second = ContextSelector.Select(stories.Reverse(), [], "Practise my FX story", now, maxBytes: 2000);
        Assert.Equal(fx.Id, first.Stories[0].Id);
        Assert.Equal(first.Stories.Select(x => x.Id), second.Stories.Select(x => x.Id));
        Assert.InRange(first.Bytes, 1, 2000);
        Assert.True(JsonSerializer.SerializeToUtf8Bytes(first).Length <= first.Bytes);
        Assert.Empty(ContextSelector.Select(stories, [], "missingword", now, requireStoryMatch: true).Stories);
        var unicode = ContextSelector.Select([Story("日本語", now, new string('界', 5000))], [], "日本語", now, maxBytes: 1000);
        Assert.True(unicode.Bytes <= 1000);
    }

    [Fact]
    public void Due_cards_prioritise_relevance_then_weakness_without_disclosing_answers()
    {
        var now = DateTimeOffset.UtcNow;
        var relevant = new LearningItem(Guid.NewGuid(), LearningDomain.Databases, "Explain transaction isolation", "PRIVATE ANSWER", 1, 5, now, now, 90);
        var weak = relevant with { Id = Guid.NewGuid(), Prompt = "Explain trees", MasteryScore = 1 };
        var future = relevant with { Id = Guid.NewGuid(), DueAt = now.AddDays(1) };
        var selected = ContextSelector.Select([], [weak, future, relevant], "transaction isolation", now);
        Assert.Equal(relevant.Id, selected.Due[0].Id);
        Assert.DoesNotContain(selected.Due, x => x.Id == future.Id);
        Assert.DoesNotContain("PRIVATE ANSWER", JsonSerializer.Serialize(selected));
    }

    [Fact]
    public async Task Voice_search_reaches_story_beyond_recent_context_without_provider_call()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException("No cloud request permitted"));
        var store = host.Services.GetRequiredService<IRuntimeStore>(); var now = DateTimeOffset.UtcNow;
        var target = Story("FX settlement", now.AddYears(-2));
        await store.UpdateAsync(s => {
            foreach (var story in Enumerable.Range(0, 15).Select(i => Story("Recent work " + i, now)).Append(target)) s.Stories.Add(story.Id, story);
            return true;
        });
        var tools = host.Services.GetRequiredService<CoreVoiceTools>();
        using var result = JsonDocument.Parse(await tools.ExecuteAsync("core_story_search", "{\"query\":\"FX\"}", Guid.NewGuid(), new HashSet<Guid>(), default));
        Assert.True(result.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(target.Id, result.RootElement.GetProperty("result")[0].GetProperty("id").GetGuid());
        Assert.Equal(1, result.RootElement.GetProperty("result")[0].GetProperty("version").GetInt32());
    }
}
