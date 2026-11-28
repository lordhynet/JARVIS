using System.Text.Json;
using Jarvis.Api.Voice;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Jarvis.Orchestrator.Training;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

// Topic-aware start for existing technical practice. No paid calls: the provider stub throws if reached.
public sealed class PracticeTopicTests
{
    private static ProviderFactory Host() => new(_ => throw new InvalidOperationException("No paid calls expected."));

    private static async Task<Guid> Session(IServiceProvider services)
    {
        var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        await services.GetRequiredService<ISessionStore>().AddAsync(new(id, ActivityType.General, "Voice fixture", SessionStatus.Active, null, null, now, now, []));
        await VoiceToolFixture.OpenAsync(services, id);
        return id;
    }
    private static async Task<Guid> Say(IServiceProvider services, Guid session, string text)
    {
        var id = Guid.NewGuid();
        await services.GetRequiredService<ISessionStore>().UpdateAsync(session, s => s with { Messages = [..s.Messages ?? [], new(id, ChatRoles.User, text, DateTimeOffset.UtcNow)] });
        return id;
    }
    private static async Task<JsonElement> StartTool(IServiceProvider services, Guid session, string mode, string? topic, string text = "Let's practise")
    {
        var utterance = await Say(services, session, text);
        using var doc = JsonDocument.Parse(await services.GetRequiredService<CoreVoiceTools>().ExecuteAsync("core_practice_start",
            JsonSerializer.Serialize(new { utterance_id = utterance, mode, topic }), session, new HashSet<Guid> { utterance }, default));
        return doc.RootElement.Clone();
    }
    private static Task<List<TrainingDrill>> Drills(IServiceProvider services) =>
        services.GetRequiredService<IRuntimeReader>().ReadAsync(s => s.TrainingDrills.Values.ToList());
    private static LearningDomain DomainOf(TrainingDrillStep step) => QuestionBank.All.Single(q => q.Id == step.QuestionId).Domain;

    [Fact]
    public void Legacy_requests_and_drills_without_a_topic_serialize_exactly_as_before()
    {
        var request = JsonSerializer.Serialize(new StartTrainingDrillRequest(Guid.Empty, Guid.Empty, TrainingMode.RapidDrill));
        Assert.DoesNotContain("Topic", request);
        var drill = JsonSerializer.Serialize(new TrainingDrill(Guid.Empty, Guid.Empty, TrainingMode.RapidDrill, null, 1, "Active", DateTimeOffset.UnixEpoch, [], []));
        Assert.DoesNotContain("RequestedTopic", drill);
        Assert.Null(JsonSerializer.Deserialize<TrainingDrill>(drill)!.RequestedTopic);
    }

    [Fact]
    public async Task Practise_system_design_by_voice_selects_only_system_design_and_records_the_topic()
    {
        using var host = Host(); var services = host.Services; var session = await Session(services);
        var result = await StartTool(services, session, "ArchitectureChallenge", "SystemDesign", "Can we practise system design now?");
        Assert.True(result.GetProperty("ok").GetBoolean(), result.GetRawText());
        var drill = Assert.Single(await Drills(services));
        Assert.Equal(LearningDomain.SystemDesign, drill.RequestedTopic);
        Assert.All(drill.Steps, step => Assert.Equal(LearningDomain.SystemDesign, DomainOf(step)));
        Assert.Contains("SystemDesign", result.GetRawText());
    }

    [Theory]
    [InlineData(LearningDomain.Databases)]
    [InlineData(LearningDomain.ApiSecurity)]
    [InlineData(LearningDomain.PaymentsFintech)]
    public async Task Topic_filtering_happens_before_selection_for_every_authored_topic(LearningDomain topic)
    {
        using var host = Host(); var services = host.Services; var session = await Session(services);
        var drills = services.GetRequiredService<TrainingDrillService>();
        var view = await drills.StartAsync(new(Guid.NewGuid(), session, TrainingMode.RapidDrill, Topic: topic), default);
        Assert.All(view.Drill.Steps, step => Assert.Equal(topic, DomainOf(step)));
    }

    [Fact]
    public async Task A_topic_without_questions_for_that_style_is_refused_without_substituting_another_topic()
    {
        using var host = Host(); var services = host.Services; var session = await Session(services);
        var error = await Assert.ThrowsAsync<DomainException>(() => services.GetRequiredService<TrainingDrillService>()
            .StartAsync(new(Guid.NewGuid(), session, TrainingMode.ArchitectureChallenge, Topic: LearningDomain.Databases), default));
        Assert.Equal(409, error.StatusCode); Assert.Contains("no authored questions for this practice style", error.Message);
        Assert.Empty(await Drills(services));
    }

    [Fact]
    public async Task Spaced_review_with_a_topic_and_nothing_due_explains_the_refusal()
    {
        using var host = Host(); var services = host.Services; var session = await Session(services);
        var error = await Assert.ThrowsAsync<DomainException>(() => services.GetRequiredService<TrainingDrillService>()
            .StartAsync(new(Guid.NewGuid(), session, TrainingMode.SpacedReview, Topic: LearningDomain.SystemDesign), default));
        Assert.Equal(409, error.StatusCode); Assert.Contains("No questions on that topic are due", error.Message);
    }

    [Theory]
    [InlineData(LearningDomain.French)]          // a LearningDomain with no authored technical questions
    [InlineData(LearningDomain.Communication)]
    [InlineData((LearningDomain)99)]
    public async Task Unsupported_or_undefined_topics_are_refused_in_Core(LearningDomain topic)
    {
        using var host = Host(); var services = host.Services; var session = await Session(services);
        var error = await Assert.ThrowsAsync<DomainException>(() => services.GetRequiredService<TrainingDrillService>()
            .StartAsync(new(Guid.NewGuid(), session, TrainingMode.RapidDrill, Topic: topic), default));
        Assert.Equal(400, error.StatusCode); Assert.Empty(await Drills(services));
    }

    [Fact]
    public async Task A_mixed_interview_keeps_its_cross_topic_contract_and_rejects_a_single_topic()
    {
        using var host = Host(); var services = host.Services; var session = await Session(services);
        var drills = services.GetRequiredService<TrainingDrillService>();
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            drills.StartAsync(new(Guid.NewGuid(), session, TrainingMode.RandomMixedInterview, Topic: LearningDomain.SystemDesign), default));
        Assert.Equal(400, error.StatusCode);
        var mixed = await drills.StartAsync(new(Guid.NewGuid(), session, TrainingMode.RandomMixedInterview), default);
        Assert.True(mixed.Drill.Steps.Select(DomainOf).Distinct().Count() > 1);
        Assert.Null(mixed.Drill.RequestedTopic);
    }

    [Fact]
    public async Task A_pinned_question_must_belong_to_the_requested_topic()
    {
        using var host = Host(); var services = host.Services; var session = await Session(services);
        var drills = services.GetRequiredService<TrainingDrillService>();
        var database = QuestionBank.All.First(q => q.Domain == LearningDomain.Databases);
        var error = await Assert.ThrowsAsync<DomainException>(() =>
            drills.StartAsync(new(Guid.NewGuid(), session, TrainingMode.RapidDrill, database.Id, LearningDomain.SystemDesign), default));
        Assert.Equal(400, error.StatusCode);
        var pinned = await drills.StartAsync(new(Guid.NewGuid(), session, TrainingMode.RapidDrill, database.Id, LearningDomain.Databases), default);
        Assert.Equal(database.Id, Assert.Single(pinned.Drill.Steps).QuestionId);
    }

    [Fact]
    public async Task Topic_is_part_of_replay_identity()
    {
        using var host = Host(); var services = host.Services; var session = await Session(services);
        var drills = services.GetRequiredService<TrainingDrillService>(); var id = Guid.NewGuid();
        var first = await drills.StartAsync(new(id, session, TrainingMode.RapidDrill, Topic: LearningDomain.Databases), default);
        Assert.Equal(first.Drill.Id, (await drills.StartAsync(new(id, session, TrainingMode.RapidDrill, Topic: LearningDomain.Databases), default)).Drill.Id);
        foreach (LearningDomain? other in new LearningDomain?[] { LearningDomain.ApiSecurity, null })
        {
            var error = await Assert.ThrowsAsync<DomainException>(() => drills.StartAsync(new(id, session, TrainingMode.RapidDrill, Topic: other), default));
            Assert.Equal(409, error.StatusCode);
        }
    }

    [Fact]
    public async Task An_active_practice_is_resumed_for_the_same_topic_and_never_silently_replaced_by_another()
    {
        using var host = Host(); var services = host.Services; var session = await Session(services);
        Assert.True((await StartTool(services, session, "RapidDrill", "Databases")).GetProperty("ok").GetBoolean());
        var other = await StartTool(services, session, "RapidDrill", "ApiSecurity");
        Assert.False(other.GetProperty("ok").GetBoolean()); Assert.Contains("A different practice is active", other.GetRawText());
        Assert.True((await StartTool(services, session, "RapidDrill", "Databases")).GetProperty("ok").GetBoolean());
        // A null topic keeps the legacy meaning: resume the active drill of the same mode.
        Assert.True((await StartTool(services, session, "RapidDrill", null)).GetProperty("ok").GetBoolean());
        Assert.False((await StartTool(services, session, "DeepDive", "Databases")).GetProperty("ok").GetBoolean());
        Assert.Single(await Drills(services));
    }

    [Fact]
    public async Task A_legacy_active_drill_without_a_topic_is_not_treated_as_any_explicit_topic()
    {
        using var host = Host(); var services = host.Services; var session = await Session(services);
        Assert.True((await StartTool(services, session, "RapidDrill", null)).GetProperty("ok").GetBoolean());
        Assert.False((await StartTool(services, session, "RapidDrill", "SystemDesign")).GetProperty("ok").GetBoolean());
        Assert.Single(await Drills(services));
    }

    [Fact]
    public async Task Concurrent_conflicting_starts_in_one_session_create_exactly_one_drill()
    {
        using var host = Host(); var services = host.Services; var session = await Session(services);
        var a = await Say(services, session, "practise databases"); var b = await Say(services, session, "practise API security");
        // Call the drill service directly so both starts reach the canonical write boundary at the same time.
        var results = await Task.WhenAll(
            Try(() => services.GetRequiredService<TrainingDrillService>().StartAsync(
                new(VoicePracticeService.Operation("start", session, a), session, TrainingMode.RapidDrill, Topic: LearningDomain.Databases), default, resumeActiveInSession: true)),
            Try(() => services.GetRequiredService<TrainingDrillService>().StartAsync(
                new(VoicePracticeService.Operation("start", session, b), session, TrainingMode.RapidDrill, Topic: LearningDomain.ApiSecurity), default, resumeActiveInSession: true)));
        Assert.Equal(1, results.Count(ok => ok));
        Assert.Single(await Drills(services));
    }

    [Theory]
    [InlineData("systemdesign")]
    [InlineData("1")]
    [InlineData("French")]
    public async Task The_voice_tool_accepts_only_exact_supported_topic_names(string topic)
    {
        using var host = Host(); var services = host.Services; var session = await Session(services);
        Assert.False((await StartTool(services, session, "RapidDrill", topic)).GetProperty("ok").GetBoolean());
        Assert.Empty(await Drills(services));
    }

    [Fact]
    public void The_tool_schema_offers_only_topics_with_authored_content_plus_null()
    {
        var start = CoreVoiceTools.Definitions.Select(d => JsonSerializer.SerializeToElement(d))
            .Single(d => d.GetProperty("name").GetString() == "core_practice_start");
        var topic = start.GetProperty("parameters").GetProperty("properties").GetProperty("topic");
        var offered = topic.GetProperty("enum").EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Null ? null : v.GetString()).ToList();
        Assert.Contains(null, offered);
        Assert.Equal(TrainingDrillService.Topics.Select(t => t.ToString()).Order(), offered.Where(v => v is not null).Order()!);
        Assert.DoesNotContain("French", offered);
        Assert.Contains("topic", start.GetProperty("parameters").GetProperty("required").EnumerateArray().Select(v => v.GetString()));
    }

    private static async Task<bool> Try(Func<Task> action)
    {
        try { await action(); return true; }
        catch (DomainException e) when (e.StatusCode == 409) { return false; }
    }
}
