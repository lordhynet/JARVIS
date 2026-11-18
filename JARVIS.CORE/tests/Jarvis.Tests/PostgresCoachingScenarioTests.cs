using Jarvis.Contracts;
using Jarvis.Orchestrator.Coaching;
using Jarvis.Orchestrator.Domains;
using Jarvis.Orchestrator.Runtime;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task Coaching_scenario_resumes_a_single_question_after_restart_and_domain_disable_is_atomic()
    {
        await using var database = await TestDatabase.CreateAsync(); Guid session;
        using (var first = new PostgresApiFactory(database.ConnectionString)) {
            var services = first.Services; session = await CoachingScenarioTests.Session(services);
            var source = await CoachingScenarioTests.Say(services, session, "Start a panel interview");
            var coach = services.GetRequiredService<VoiceCoachingService>();
            await coach.RunAsync(session, "start", source, "scenario-panel", null, null, null, default);
            source = await CoachingScenarioTests.Say(services, session, "I chose an alternative because we had a constraint.");
            var next = await coach.RunAsync(session, "answer", source, null, null, null, null, default);
            Assert.Equal(2, next!.QuestionNumber); Assert.Equal("Question", next.Status);
        }
        using var restarted = new PostgresApiFactory(database.ConnectionString); var current = await restarted.Services.GetRequiredService<VoiceCoachingService>()
            .RunAsync(session, "current", Guid.Empty, null, null, null, null, default);
        Assert.Equal(2, current!.QuestionNumber); Assert.Equal(1, current.Scenario!.Answers);
        var registry = restarted.Services.GetRequiredService<IDomainRegistry>(); var preview = await registry.PreviewAsync("scenario-panel", null, default);
        using (CoreInvocationContext.Enter(InvocationOrigin.ManualClient)) await registry.DisableAsync("scenario-panel", new(Guid.NewGuid(), preview.Definition.Revision), new("owner", ["domain.manage"]), default);
        var runtime = restarted.Services.GetRequiredService<IRuntimeStore>();
        Assert.Equal("Cancelled", await runtime.ReadAsync(s => s.VoiceCoachings.Values.Single().Status));
        Assert.Single(await runtime.ReadAsync(s => s.CoachingAttempts.Values.ToArray()));
    }
}
