using System.Text.Json;
using System.Text;
using Jarvis.Api.Voice;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed class VoiceRecallTests
{
    private static async Task<(Guid Session, Guid Card)> Setup(IServiceProvider services)
    {
        var session = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        await services.GetRequiredService<ISessionStore>().AddAsync(new(session, ActivityType.General, "Recall fixture", SessionStatus.Active, null, null, now, now, []));
        await VoiceToolFixture.OpenAsync(services, session);
        var card = await services.GetRequiredService<LearningService>().CreateAsync(new(LearningDomain.French, "Say hello in French", "Bonjour"), default);
        return (session, card.Id);
    }
    private static async Task<Guid> Say(IServiceProvider services, Guid session, string text)
    {
        var id = Guid.NewGuid(); await services.GetRequiredService<ISessionStore>().UpdateAsync(session,
            s => s with { Messages = [..s.Messages ?? [], new(id, "user", text, DateTimeOffset.UtcNow)] }); return id;
    }
    [Fact]
    public async Task Recall_requires_attempt_then_separate_explicit_rating_and_schedules_once()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException()); var services = host.Services;
        var (session, card) = await Setup(services); var recall = services.GetRequiredService<VoiceRecallService>();
        var start = await Say(services, session, "Review my due cards");
        var question = await recall.RunAsync(session, "start", start, "None", default);
        Assert.NotNull(question); Assert.Equal(card, question.CardId); Assert.Null(question.Reference);
        Assert.Equal(question, await recall.RunAsync(session, "start", start, "None", default));
        await Assert.ThrowsAsync<DomainException>(() => recall.RunAsync(session, "reveal", start, "None", default));
        await Assert.ThrowsAsync<DomainException>(() => recall.RunAsync(session, "rate", start, "Good", default));
        var answer = await Say(services, session, "Bonjour");
        var reference = await recall.RunAsync(session, "reveal", answer, "None", default);
        Assert.Equal("Bonjour", reference!.Reference);
        await Assert.ThrowsAsync<DomainException>(() => recall.RunAsync(session, "rate", answer, "Good", default));
        var rating = await Say(services, session, "Good.");
        var done = await recall.RunAsync(session, "rate", rating, "Good", default);
        Assert.Equal("Completed", done!.Status); Assert.Equal("Good", done.Rating); Assert.NotNull(done.NextDueAt);
        Assert.Equal(done, await recall.RunAsync(session, "rate", rating, "Good", default));
        var saved = Assert.Single(await services.GetRequiredService<IRuntimeReader>().ReadAsync(s => s.Reviews.Values.ToArray()));
        Assert.Equal("not_measured", saved.Event.TimingSource); Assert.Equal(TimeSpan.Zero, saved.Event.ResponseTime);
        Assert.Equal(card, saved.Event.LearningItemId); Assert.True(saved.Result.DueAt > DateTimeOffset.UtcNow);
    }
    [Theory]
    [InlineData("I don't think that was good", "Good")]
    [InlineData("Good or hard?", "Good")]
    [InlineData("Hard", "Easy")]
    public async Task Recall_does_not_infer_or_override_a_users_rating(string spoken, string proposed)
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException()); var services = host.Services;
        var (session, _) = await Setup(services); var recall = services.GetRequiredService<VoiceRecallService>();
        await recall.RunAsync(session, "start", await Say(services, session, "Review"), "None", default);
        await recall.RunAsync(session, "reveal", await Say(services, session, "Bonjour"), "None", default);
        var rating = await Say(services, session, spoken);
        await Assert.ThrowsAsync<DomainException>(() => recall.RunAsync(session, "rate", rating, proposed, default));
        Assert.Empty(await services.GetRequiredService<IRuntimeReader>().ReadAsync(s => s.Reviews));
    }
    [Fact]
    public async Task Card_changed_elsewhere_blocks_stale_reveal_and_stop_does_not_reschedule()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException()); var services = host.Services;
        var (session, card) = await Setup(services); var recall = services.GetRequiredService<VoiceRecallService>();
        await recall.RunAsync(session, "start", await Say(services, session, "Review"), "None", default);
        var learning = services.GetRequiredService<LearningService>();
        await learning.ReviewAsync(card, new(Guid.NewGuid(), ReviewRating.Hard, 12), default);
        var source = await Say(services, session, "Bonjour");
        await Assert.ThrowsAsync<DomainException>(() => recall.RunAsync(session, "reveal", source, "None", default));
        Assert.Equal("Cancelled", (await recall.RunAsync(session, "stop", source, "None", default))!.Status);
        Assert.Null(await recall.RunAsync(session, "current", Guid.Empty, "None", default));
        Assert.Single(await services.GetRequiredService<IRuntimeReader>().ReadAsync(s => s.Reviews.Values.ToArray()));
    }
    [Fact]
    public async Task Recall_tool_is_window_scoped_and_reference_hidden_until_user_attempt()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException()); var services = host.Services;
        var (session, _) = await Setup(services); var tools = services.GetRequiredService<CoreVoiceTools>();
        var start = await Say(services, session, "Review");
        var args = JsonSerializer.Serialize(new { action = "start", utterance_id = start, rating = "None" });
        using var denied = JsonDocument.Parse(await tools.ExecuteAsync("core_recall", args, session, new HashSet<Guid>(), default));
        Assert.False(denied.RootElement.GetProperty("ok").GetBoolean());
        var json = await tools.ExecuteAsync("core_recall", args, session, new HashSet<Guid> { start }, default);
        using var allowed = JsonDocument.Parse(json); Assert.True(allowed.RootElement.GetProperty("ok").GetBoolean()); Assert.DoesNotContain("Bonjour", json);
        var answer = await Say(services, session, "Salut");
        var reveal = await tools.ExecuteAsync("core_recall", JsonSerializer.Serialize(new { action = "reveal", utterance_id = answer, rating = "None" }), session, new HashSet<Guid> { answer }, default);
        Assert.Contains("Bonjour", reveal);
    }
    [Fact]
    public async Task Recall_metadata_exports_and_is_removed_with_its_retained_voice_sources_and_erasure()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException()); var services = host.Services;
        var (session, _) = await Setup(services); var recall = services.GetRequiredService<VoiceRecallService>();
        var source = await Say(services, session, "Review"); await recall.RunAsync(session, "start", source, "None", default);
        var privacy = services.GetRequiredService<IPrivacyReader>();
        var export = await privacy.ExportAsync(DateTimeOffset.UtcNow); Assert.Single(export.VoiceRecalls!);
        var runtime = services.GetRequiredService<IRuntimeStore>();
        await runtime.UpdateAsync(s => {
            s.VoiceTurns[source] = new(source, session, "user", "Review", null, DateTimeOffset.UtcNow.AddDays(-31));
            var selection = PrivacyData.Select(s, [], [], new(Enabled: true), DateTimeOffset.UtcNow);
            PrivacyData.RetainRuntime(s, new(Enabled: true), DateTimeOffset.UtcNow, selection); return true;
        });
        Assert.Empty(await runtime.ReadAsync(s => s.VoiceRecalls));
        // Erasure creates a fresh runtime with no recall metadata.
        var empty = PrivacyData.EmptyAfterErase(new RuntimeState { VoiceRecalls = export.VoiceRecalls!.ToDictionary(r => r.Id) });
        Assert.Empty(empty.VoiceRecalls);
    }
    [Fact]
    public async Task Long_unicode_card_content_remains_bounded_and_explicitly_marked_incomplete()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException()); var services = host.Services;
        var (session, card) = await Setup(services);
        await services.GetRequiredService<IRuntimeStore>().UpdateAsync(s => { s.Learning[card] = s.Learning[card] with {
            Prompt = new string('界', 1500), Answer = new string('答', 3000) }; return true; });
        var tools = services.GetRequiredService<CoreVoiceTools>();
        foreach (var (action, text) in new[] { ("start", "Review my cards"), ("reveal", "My attempted answer") })
        {
            var source = await Say(services, session, text);
            var json = await tools.ExecuteAsync("core_recall", JsonSerializer.Serialize(new { action, utterance_id = source, rating = "None" }), session, new HashSet<Guid> { source }, default);
            Assert.True(Encoding.UTF8.GetByteCount(json) <= CoreVoiceTools.MaxOutputBytes);
            using var result = JsonDocument.Parse(json); Assert.True(result.RootElement.GetProperty("ok").GetBoolean());
            Assert.True(result.RootElement.GetProperty("result").GetProperty("contentTruncated").GetBoolean());
        }
    }
}
