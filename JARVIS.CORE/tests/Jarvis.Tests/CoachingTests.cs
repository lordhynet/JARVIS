using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Coaching;
using Jarvis.Orchestrator.Cqrs.Coaching;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;

namespace Jarvis.Tests;

public sealed class CoachingTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.Parse("2026-11-13T12:00Z");
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Fixture
    {
        public readonly Clock Clock = new(); public readonly InMemoryRuntimeStore Store = new(); public readonly InMemorySessionStore Sessions = new();
        public readonly Guid SessionId = Guid.NewGuid(); public CoachingService Coach => new(Store, Sessions, Clock);
        public ProgrammeService Programmes => new(Store, Clock);
        public async Task<SaveCoachingAnswerRequest> Request()
        {
            if (await Sessions.GetAsync(SessionId) is null) await Sessions.AddAsync(new(SessionId, ActivityType.Communication, "Coaching test", SessionStatus.Active, null, null, Clock.Now, Clock.Now, []));
            return new(Guid.NewGuid(), SessionId, "point-first", 1, "Um, we basically gathered requirements and we deployed the service.");
        }
        public CoachingQueryHandlers Queries => new(Store, Sessions, Clock);
    }
    [Fact]
    public void Catalog_has_communication_interview_programme_and_honest_planned_modules()
    {
        Assert.Equal(9, CoachingCatalog.Exercises.Count(x => x.Area == CoachingArea.Communication));
        Assert.Equal(19, CoachingCatalog.Exercises.Count(x => x.Area == CoachingArea.Interview));
        Assert.Equal(12, CoachingCatalog.Weeks.Count); Assert.Equal(15, ProductModules.All.Count);
        Assert.All(ProductModules.All.Where(x => x.Status == "Planned"), x => { Assert.Empty(x.Available); Assert.NotEmpty(x.Remaining); });
        Assert.Equal(CoachingCatalog.Exercises.Count, CoachingCatalog.Exercises.Select(x => x.Id).Distinct().Count());
    }
    [Fact]
    public void Text_observations_have_evidence_and_do_not_invent_audio_or_confidence()
    {
        var result = TextCoach.Analyse("Um, we basically deployed the service. We verified the result.", null);
        Assert.Equal(2, result.FillerCandidates); Assert.Null(result.ApproximateWordsPerMinute);
        Assert.Equal("unavailable", result.TimingSource);
        Assert.Contains(result.Observations, x => x.Code == "ownership-language" && x.Evidence.Contains("we"));
        Assert.DoesNotContain(result.Observations, x => x.Code.Contains("confidence") || x.Code.Contains("correctness"));
        Assert.Equal(120, TextCoach.Analyse("One two three four", 2).ApproximateWordsPerMinute);
        Assert.Equal("user_reported_elapsed", TextCoach.Analyse("One two three four", 2).TimingSource);
        Assert.Equal(0, TextCoach.Analyse("The album has an unusual rhythm.", null).FillerCandidates);
        Assert.Equal(1, TextCoach.Analyse("I’ve explained my decision.", null).FirstPersonSingularCount);
    }
    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(3601)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public void Invalid_reported_durations_are_rejected(double seconds) => Assert.Throws<DomainException>(() => CoachingService.Duration(seconds));
    [Fact]
    public async Task Concurrent_retries_save_one_answer_and_conflicting_payload_is_rejected()
    {
        var f = new Fixture(); var request = await f.Request();
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => f.Coach.SaveAsync(request, default)));
        Assert.Single(results.Select(x => x.Attempt.Id).Distinct()); Assert.Single(await f.Store.ReadAsync(s => s.CoachingAttempts));
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => f.Coach.SaveAsync(request with { Text = "Different" }, default))).StatusCode);
        Assert.Null(results[0].Analysis.ApproximateWordsPerMinute);
    }
    [Fact]
    public async Task Corrections_are_versioned_recomputed_and_limited_to_five()
    {
        var f = new Fixture(); var a = await f.Coach.SaveAsync(await f.Request(), default);
        var request = new CorrectCoachingAnswerRequest(Guid.NewGuid(), 1, "I owned the database migration and verified the result.");
        var updated = await f.Coach.CorrectAsync(a.Attempt.Id, request, default);
        Assert.Equal(0, updated.Analysis.FillerCandidates); Assert.Equal(2, updated.Attempt.Answers.Count);
        Assert.DoesNotContain(updated.Analysis.Observations, x => x.Code == "ownership-language");
        Assert.Equal(2, (await f.Coach.CorrectAsync(a.Attempt.Id, request, default)).Attempt.Revision);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => f.Coach.CorrectAsync(a.Attempt.Id, request with { RequestId = Guid.NewGuid() }, default))).StatusCode);
        for (var i = 0; i < 3; i++) updated = await f.Coach.CorrectAsync(a.Attempt.Id, new(Guid.NewGuid(), updated.Attempt.Revision, $"Version {i}"), default);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => f.Coach.CorrectAsync(a.Attempt.Id, new(Guid.NewGuid(), updated.Attempt.Revision, "Too many"), default))).StatusCode);
    }
    [Fact]
    public async Task Concurrent_distinct_corrections_have_one_winner()
    {
        var f = new Fixture(); var a = await f.Coach.SaveAsync(await f.Request(), default);
        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(async i =>
        {
            try { await f.Coach.CorrectAsync(a.Attempt.Id, new(Guid.NewGuid(), 1, $"Correction {i}"), default); return true; }
            catch (DomainException e) { Assert.Equal(409, e.StatusCode); return false; }
        }));
        Assert.Single(results, x => x);
    }
    [Fact]
    public async Task Deletion_purges_text_and_late_retries_cannot_resurrect_it()
    {
        var f = new Fixture(); var request = await f.Request(); var a = await f.Coach.SaveAsync(request, default);
        Assert.True(await f.Coach.DeleteAsync(a.Attempt.Id, 1, default)); Assert.False(await f.Coach.DeleteAsync(a.Attempt.Id, 1, default));
        Assert.Empty((await f.Store.ReadAsync(s => s.CoachingAttempts[a.Attempt.Id])).Answers);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => f.Coach.SaveAsync(request, default))).StatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<DomainException>(() => f.Queries.HandleAsync(new GetCoaching(a.Attempt.Id)))).StatusCode);
    }
    [Fact]
    public async Task CV_prompts_require_real_current_claims_and_do_not_copy_claims_into_attempt_metadata()
    {
        var f = new Fixture(); var baseRequest = await f.Request(); var story = new Story(Guid.NewGuid(), new("Real story", "Project", "Service", CvClaims: ["unique confidential claim 1234"]), 1, f.Clock.Now, f.Clock.Now);
        await f.Store.UpdateAsync(s => { s.Stories.Add(story.Id, story); return true; });
        var prompt = await f.Queries.HandleAsync(new GetCoachingPrompt("cv-defence", story.Id, 1, 0));
        Assert.Contains(story.Content.CvClaims![0], prompt.Question);
        var a = await f.Coach.SaveAsync(baseRequest with { ExerciseId = "cv-defence", StoryId = story.Id, StoryVersion = 1, ClaimIndex = 0 }, default);
        Assert.DoesNotContain(story.Content.CvClaims[0], JsonSerializer.Serialize(a.Attempt));
        await f.Store.UpdateAsync(s => { s.Stories[story.Id] = story with { Version = 2 }; return true; });
        Assert.False((await f.Queries.HandleAsync(new GetCoaching(a.Attempt.Id))).SourceCurrent);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => f.Coach.CorrectAsync(a.Attempt.Id, new(Guid.NewGuid(), 1, "Revised"), default))).StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => f.Coach.SaveAsync(baseRequest with { RequestId = Guid.NewGuid(), ExerciseId = "cv-defence", StoryId = story.Id, StoryVersion = 1, ClaimIndex = 0 }, default))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<DomainException>(() => f.Queries.HandleAsync(new GetCoachingPrompt("cv-defence", story.Id, 2, 9)))).StatusCode);
    }
    [Fact]
    public async Task Session_reports_use_current_versions_and_exclude_deleted_answers()
    {
        var f = new Fixture(); var a = await f.Coach.SaveAsync(await f.Request(), default);
        var first = await f.Queries.HandleAsync(new GetSessionReview(f.SessionId));
        Assert.Equal(1, first.CoachingAttempts); Assert.Contains(first.TextObservations, x => x.Code == "ownership-language");
        await f.Coach.CorrectAsync(a.Attempt.Id, new(Guid.NewGuid(), 1, "I personally designed the schema and tested the migration."), default);
        var second = await f.Queries.HandleAsync(new GetSessionReview(f.SessionId));
        Assert.NotEqual(first.Fingerprint, second.Fingerprint); Assert.DoesNotContain(second.TextObservations, x => x.Code == "ownership-language");
        await f.Coach.DeleteAsync(a.Attempt.Id, 2, default);
        Assert.Equal(0, (await f.Queries.HandleAsync(new GetSessionReview(f.SessionId))).CoachingAttempts);
        Assert.Equal(404, (await Assert.ThrowsAsync<DomainException>(() => f.Queries.HandleAsync(new GetSessionReview(Guid.NewGuid())))).StatusCode);
    }
    [Fact]
    public async Task Programme_is_timezone_aware_retry_safe_and_does_not_allow_future_or_duplicate_weeks()
    {
        var f = new Fixture(); var start = new StartProgrammeRequest(Guid.NewGuid(), "2026-11-13");
        var programme = await f.Programmes.StartAsync(start, default); Assert.Equal(1, ProgrammeService.Week(programme, f.Clock.Now));
        var check = new ProgrammeCheckInRequest(Guid.NewGuid(), 1, 1, "My reflection");
        programme = await f.Programmes.CheckInAsync(programme.Id, check, default);
        Assert.Single((await f.Programmes.CheckInAsync(programme.Id, check, default)).CheckIns);
        Assert.Equal(2, (await f.Programmes.StartAsync(start, default)).Revision);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => f.Programmes.CheckInAsync(programme.Id, check with { RequestId = Guid.NewGuid(), ExpectedRevision = 2 }, default))).StatusCode);
        Assert.Equal(400, (await Assert.ThrowsAsync<DomainException>(() => f.Programmes.CheckInAsync(programme.Id, new(Guid.NewGuid(), 2, 2, "Too early"), default))).StatusCode);
        f.Clock.Now = f.Clock.Now.AddDays(7); Assert.Equal(2, ProgrammeService.Week(programme, f.Clock.Now));
        programme = await f.Programmes.CheckInAsync(programme.Id, new(Guid.NewGuid(), 2, 2, "Second reflection"), default); Assert.Equal(2, programme.CheckIns.Count);
        await f.Programmes.DeleteAsync(programme.Id, programme.Revision, default);
        Assert.Null((await f.Queries.HandleAsync(new GetProgramme())).Programme);
        Assert.Empty((await f.Store.ReadAsync(s => s.Programmes[programme.Id])).CheckIns);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => f.Programmes.StartAsync(start, default))).StatusCode);
    }
    [Fact]
    public void Programme_calendar_handles_local_date_boundaries_and_invalid_dates()
    {
        var plan = new ImprovementProgramme(Guid.NewGuid(), "2026-11-13", "Pacific/Auckland", 1, DateTimeOffset.UtcNow, []);
        Assert.Equal(1, ProgrammeService.Week(plan, DateTimeOffset.Parse("2026-11-12T12:00Z")));
        Assert.Equal(0, ProgrammeService.Week(plan, DateTimeOffset.Parse("2026-11-11T12:00Z")));
        Assert.Equal(13, ProgrammeService.Week(plan, DateTimeOffset.Parse("2027-11-12T12:00Z")));
        Assert.Throws<DomainException>(() => ProgrammeService.StartDate("2026-02-30"));
    }
    [Fact]
    public async Task Deleting_story_purges_linked_coaching_answers_and_session_report_evidence()
    {
        using var factory = new JarvisApiFactory(); using var client = factory.CreateAuthorisedClient();
        var session = await LifeScenarioTests.Post<ConversationSession>(client, "/api/sessions", new CreateSessionRequest(ActivityType.MockInterview, "Linked coaching", null));
        var story = await LifeScenarioTests.Post<Story>(client, "/api/stories", new CreateStoryRequest(Guid.NewGuid(), new("Real story", "Company", "Service", CvClaims: ["A real claim"])));
        var request = new SaveCoachingAnswerRequest(Guid.NewGuid(), session.Id, "cv-defence", 1, "We basically built a service", story.Id, 1, 0);
        var a = await LifeScenarioTests.Post<CoachingAttemptView>(client, "/api/coaching/attempts", request);
        using var deletion = await client.DeleteAsync($"/api/stories/{story.Id}?expectedVersion=1"); deletion.EnsureSuccessStatusCode();
        using var missing = await client.GetAsync($"/api/coaching/attempts/{a.Attempt.Id}"); Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        var review = (await client.GetFromJsonAsync<SessionReview>($"/api/sessions/{session.Id}/review", LifeScenarioTests.Json))!;
        Assert.Equal(0, review.CoachingAttempts); Assert.Empty(review.TextObservations);
        using var retry = await client.PostAsJsonAsync("/api/coaching/attempts", request, LifeScenarioTests.Json); Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
    }
}
