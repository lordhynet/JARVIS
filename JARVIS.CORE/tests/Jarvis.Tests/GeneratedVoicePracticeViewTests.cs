using Jarvis.Contracts;
using Jarvis.Orchestrator.Coaching;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Jarvis.Orchestrator.Training;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

// Voice practice must stay safe when a generated drill is waiting for its next question (no current step to index).
public sealed class GeneratedVoicePracticeViewTests
{
    private static async Task<Guid> Say(IServiceProvider services, Guid session, string text)
    {
        var id = Guid.NewGuid();
        await services.GetRequiredService<VoiceTranscriptService>().RecordAsync(session, id, ChatRoles.User, text, 30, default,
            restricted: false, timing: new(30, 6, SpeechDelivery.PcmQuietMethod));
        return id;
    }

    private static async Task<(IServiceProvider Services, Guid Session, TrainingDrill Drill)> Awaiting(ProviderFactory host)
    {
        var services = host.Services;
        var session = (await services.GetRequiredService<SessionService>().CreateAsync(new(ActivityType.General, "Practice fixture", null))).Id;
        await VoiceToolFixture.OpenAsync(services, session);
        var trigger = await Say(services, session, "Can we practise system design?");
        var drill = GeneratedDrillFlow.Start(Guid.NewGuid(), session, TrainingMode.RapidDrill, LearningDomain.SystemDesign,
            PracticeOutlines.ByTopic[LearningDomain.SystemDesign][0], new(trigger, 0, "openai/fixture/p/r"), DateTimeOffset.UtcNow);
        await services.GetRequiredService<IRuntimeStore>().UpdateAsync(s => { s.TrainingDrills[drill.Id] = drill; return true; });
        return (services, session, drill);
    }

    [Fact]
    public async Task Current_practice_reports_the_awaited_outline_entry_without_a_prompt()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException("No provider call"));
        var (services, session, drill) = await Awaiting(host);
        var view = (await services.GetRequiredService<VoicePracticeService>().CurrentAsync(session, default))!;
        Assert.Equal(drill.Id, view.DrillId); Assert.Equal("requirements", view.AwaitingOutline);
        Assert.Null(view.Prompt); Assert.False(view.AiGenerated); Assert.Equal(0, view.Total);
        
    }

    [Fact]
    public async Task Answering_before_a_generated_question_is_asked_is_refused_and_saves_nothing()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException("No provider call"));
        var (services, session, _) = await Awaiting(host);
        var answer = await Say(services, session, "I would start with requirements.");
        var error = await Assert.ThrowsAsync<DomainException>(() => services.GetRequiredService<VoicePracticeService>().AnswerAsync(session, answer, default));
        Assert.Equal(409, error.StatusCode); Assert.Contains("asked yet", error.Message);
        Assert.Empty(await services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.TrainingAttempts.Values.Where(a => a.SessionId == session).ToArray()));
    }

    [Fact]
    public async Task Resuming_the_same_practice_returns_the_awaiting_view_instead_of_failing()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException("No provider call"));
        var (services, session, drill) = await Awaiting(host);
        var again = await Say(services, session, "Let's carry on with system design.");
        var view = await services.GetRequiredService<VoicePracticeService>().StartAsync(session, again, TrainingMode.RapidDrill, default, LearningDomain.SystemDesign);
        Assert.Equal(drill.Id, view.DrillId); Assert.Equal("requirements", view.AwaitingOutline);
    }

    [Fact]
    public async Task Once_the_question_is_applied_the_view_shows_it_labelled_as_AI_generated()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException("No provider call"));
        var (services, session, drill) = await Awaiting(host);
        var question = Guid.NewGuid();
        await services.GetRequiredService<IRuntimeStore>().UpdateAsync(s =>
        {
            s.TrainingDrills[drill.Id] = GeneratedDrillFlow.ApplyQuestion(drill, drill.Generated!.Pending!,
                new(question, 1, "Clarifying requirements", "What do you clarify first?", "Scale and latency.", []));
            return true;
        });
        var view = (await services.GetRequiredService<VoicePracticeService>().CurrentAsync(session, default))!;
        Assert.Equal("What do you clarify first?", view.Prompt); Assert.True(view.AiGenerated); Assert.Null(view.AwaitingOutline); 
    }
}
