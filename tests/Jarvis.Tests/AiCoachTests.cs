using System.Net;
using System.Text;
using System.Text.Json;
using Jarvis.Api.Integrations;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Coaching;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Jarvis.Orchestrator.Training;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed class AiCoachTests
{
    [Fact]
    public async Task Guided_assessment_uses_its_saved_code_context_and_deletion_purges_cached_quotes()
    {
        using var host = new ProviderFactory(async request =>
        {
            using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            using var context = JsonDocument.Parse(payload.RootElement.GetProperty("input").GetString()!);
            Assert.Contains("Review the fragment", context.RootElement.GetProperty("Question").GetString());
            Assert.Contains(".Result", context.RootElement.GetProperty("Code").GetString());
            return Response("I owned the migration");
        });
        using var client = host.Client(); var sessionId = Guid.NewGuid(); var now = DateTimeOffset.UtcNow;
        await host.Services.GetRequiredService<ISessionStore>().AddAsync(new(sessionId, ActivityType.General, "Review", SessionStatus.Active, null, null, now, now, []));
        var view = await host.Services.GetRequiredService<TrainingDrillService>().StartAsync(new(Guid.NewGuid(), sessionId, TrainingMode.CodeReview, QuestionBank.All[0].Id), default);
        var training = host.Services.GetRequiredService<TrainingService>(); var step = view.Drill.Steps[0];
        var answer = await training.SubmitAsync(new(Guid.NewGuid(), sessionId, step.QuestionId, step.QuestionVersion, view.Drill.Mode,
            "I owned the migration and verified recovery.", DrillId: view.Drill.Id, StepIndex: 0), default);
        var assessed = await host.Services.GetRequiredService<AiCoach>().TechnicalAsync(answer.Id, new(Guid.NewGuid(), answer.Revision, answer.TextRevision, true), default);
        var store = host.Services.GetRequiredService<IRuntimeStore>(); Assert.Single(await store.ReadAsync(s => s.ProviderResults));
        await training.DeleteAsync(answer.Id, assessed.Revision, default);
        Assert.Empty(await store.ReadAsync(s => s.ProviderResults)); Assert.Single(await store.ReadAsync(s => s.UsageReservations));
        Assert.Empty(await store.ReadAsync(s => s.TrainingAttempts[answer.Id].Answers));
    }
    private static HttpResponseMessage Response(string evidence) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            status = "completed", usage = new { input_tokens = 100, output_tokens = 100 },
            output = new[] { new { content = new[] { new { type = "output_text", text = JsonSerializer.Serialize(new
            {
                summary = "Make the result concrete.", followUps = new[] { "What evidence supports the outcome?" },
                scores = new[] { new { dimension = "TechnicalAccuracy", value = 4, evidence, rationale = "A personal claim is not a technical reference." },
                    new { dimension = "PersonalOwnership", value = 3, evidence, rationale = "The answer states ownership." } }
            }) } } } }
        }), Encoding.UTF8, "application/json")
    };

    private static async Task<CoachingAttemptView> Answer(ProviderFactory factory)
    {
        var now = DateTimeOffset.UtcNow; var id = Guid.NewGuid();
        await factory.Services.GetRequiredService<ISessionStore>().AddAsync(new(id, ActivityType.Communication, "Coaching", SessionStatus.Active, null, null, now, now, []));
        return await factory.Services.GetRequiredService<CoachingService>().SaveAsync(new(Guid.NewGuid(), id, "point-first", 1, "I owned the migration and verified recovery."), default);
    }

    [Fact]
    public async Task Consent_evidence_replay_and_answer_versions_are_enforced()
    {
        var calls = 0; using var factory = new ProviderFactory(_ => { calls++; return Task.FromResult(Response("I owned the migration")); });
        using var client = factory.Client(); var answer = await Answer(factory); var ai = factory.Services.GetRequiredService<AiCoach>();
        var request = new AiAssessmentRequest(Guid.NewGuid(), answer.Attempt.Revision, 1, true);
        Assert.Equal(400, (await Assert.ThrowsAsync<DomainException>(() => ai.CoachingAsync(answer.Attempt.Id, request with { AllowCloudProcessing = false }, default))).StatusCode);
        Assert.Equal(0, calls);
        var assessed = await ai.CoachingAsync(answer.Attempt.Id, request, default);
        Assert.Equal(1, calls); Assert.NotNull(assessed.AiAssessment);
        Assert.Null(assessed.AiAssessment.Scores.Single(x => x.Dimension == AssessmentDimension.TechnicalAccuracy).Value);
        Assert.Equal(3, assessed.AiAssessment.Scores.Single(x => x.Dimension == AssessmentDimension.PersonalOwnership).Value);
        await ai.CoachingAsync(answer.Attempt.Id, request, default); Assert.Equal(1, calls);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => ai.CoachingAsync(answer.Attempt.Id, request with { TextRevision = 2 }, default))).StatusCode);
        var corrected = await factory.Services.GetRequiredService<CoachingService>().CorrectAsync(answer.Attempt.Id, new(Guid.NewGuid(), assessed.Attempt.Revision, "I corrected the evidence."), default);
        Assert.Null(corrected.AiAssessment); Assert.Null(corrected.Attempt.AiAssessmentId);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => ai.CoachingAsync(answer.Attempt.Id, request, default))).StatusCode);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Fabricated_quotes_are_rejected_without_automatic_paid_replay()
    {
        var calls = 0; using var factory = new ProviderFactory(_ => { calls++; return Task.FromResult(Response("I saved millions of pounds")); });
        using var client = factory.Client(); var answer = await Answer(factory); var ai = factory.Services.GetRequiredService<AiCoach>();
        var request = new AiAssessmentRequest(Guid.NewGuid(), answer.Attempt.Revision, 1, true);
        for (var i = 0; i < 2; i++) Assert.Equal(502, (await Assert.ThrowsAsync<DomainException>(() => ai.CoachingAsync(answer.Attempt.Id, request, default))).StatusCode);
        Assert.Equal(1, calls);
        var store = factory.Services.GetRequiredService<IRuntimeStore>(); Assert.Empty(await store.ReadAsync(s => s.AiCoachAssessments));
    }

    [Fact]
    public async Task Correction_during_provider_processing_cannot_attach_stale_feedback()
    {
        ProviderFactory? factory = null; CoachingAttemptView? answer = null;
        using var host = factory = new ProviderFactory(async _ =>
        {
            await factory!.Services.GetRequiredService<CoachingService>().CorrectAsync(answer!.Attempt.Id, new(Guid.NewGuid(), answer.Attempt.Revision, "A changed answer."), default);
            return Response("I owned the migration");
        });
        using var client = host.Client(); answer = await Answer(host);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => host.Services.GetRequiredService<AiCoach>().CoachingAsync(answer.Attempt.Id, new(Guid.NewGuid(), answer.Attempt.Revision, 1, true), default))).StatusCode);
        Assert.Empty(await host.Services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.AiCoachAssessments));
    }

    [Fact]
    public async Task Concurrent_coaching_assessments_claim_one_slot_before_provider_dispatch()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var host = new ProviderFactory(async _ =>
        {
            Interlocked.Increment(ref calls); entered.TrySetResult(); await release.Task;
            return Response("I owned the migration");
        });
        using var client = host.Client(); var answer = await Answer(host); var ai = host.Services.GetRequiredService<AiCoach>();
        var requests = Enumerable.Range(0, 6).Select(_ => new AiAssessmentRequest(Guid.NewGuid(), answer.Attempt.Revision, 1, true)).ToArray();
        var tasks = requests.Select(async request =>
        {
            try { await ai.CoachingAsync(answer.Attempt.Id, request, default); return 200; }
            catch (DomainException exception) { return exception.StatusCode; }
        }).ToArray();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(150);
        release.TrySetResult();
        var results = await Task.WhenAll(tasks);
        Assert.Equal(1, calls);
        Assert.Equal(1, results.Count(status => status == 200));
        Assert.Equal(5, results.Count(status => status == 409));
        var state = await host.Services.GetRequiredService<IRuntimeStore>().ReadAsync(s => (s.AiCoachAssessments.Count, s.AiCoachAssessmentClaims.Count));
        Assert.Equal((1, 0), state);
    }

    [Fact]
    public async Task Deletion_during_provider_processing_cannot_recreate_private_provider_cache()
    {
        ProviderFactory? factory = null; CoachingAttemptView? answer = null;
        using var host = factory = new ProviderFactory(async _ =>
        {
            await factory!.Services.GetRequiredService<CoachingService>().DeleteAsync(answer!.Attempt.Id, answer.Attempt.Revision, default);
            return Response("I owned the migration");
        });
        using var client = host.Client(); answer = await Answer(host);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => host.Services.GetRequiredService<AiCoach>().CoachingAsync(answer.Attempt.Id,
            new(Guid.NewGuid(), answer.Attempt.Revision, 1, true), default))).StatusCode);
        var store = host.Services.GetRequiredService<IRuntimeStore>(); Assert.Empty(await store.ReadAsync(s => s.ProviderResults));
        Assert.Single(await store.ReadAsync(s => s.UsageReservations));
    }
}
