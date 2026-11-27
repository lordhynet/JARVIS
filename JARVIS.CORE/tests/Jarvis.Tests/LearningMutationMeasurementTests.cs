using System.Diagnostics;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Jarvis.Orchestrator.Training;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Jarvis.Tests;

internal sealed record LearningMutationFixture(RuntimeState State, Guid Question, Guid Session, Guid CreationRequest,
    Guid LateReview, Guid[] Corrections, Guid[] Deletions)
{
    internal static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-03-28T12:00:00Z");
    internal static LearningMutationFixture Make(int count, int iterations)
    {
        var s = new RuntimeState(); var question = QuestionBank.All[0]; var session = Guid.NewGuid();
        var original = new LearningItem(question.Id, question.Domain, question.Prompt, question.Reference, 0, 5, At.AddDays(-5), At.AddDays(-5));
        var baseline = original; var included = new HashSet<Guid>();
        for (var i = 0; i < 2; i++) {
            var id = Guid.NewGuid(); var reviewedAt = At.AddDays(i - 3); baseline = TrainingProjection.Manual(baseline, ReviewRating.Again, reviewedAt);
            s.Reviews.Add(id, new(id, new(Guid.NewGuid(), question.Id, ReviewRating.Again, TimeSpan.FromSeconds(2), reviewedAt), baseline)); included.Add(id);
        }
        s.TrainingBaselines[question.Id] = baseline; s.TrainingBaselineReviews[question.Id] = included;
        var lateReview = Guid.NewGuid();
        s.Reviews[lateReview] = new(lateReview, new(Guid.NewGuid(), question.Id, ReviewRating.Good, TimeSpan.FromSeconds(2), At.AddDays(1)), baseline);
        s.TrainingManualSequences[lateReview] = count + 1; s.NextTrainingSequence = count + 100;
        Guid AddCard(LearningItem card) {
            s.Learning.Add(card.Id, card); var id = Guid.NewGuid(); var request = new CreateLearningRequest(card.Domain, card.Prompt, card.Answer, id);
            s.CreationReceipts.Add(id, new(id, 0, LearningCreationReceipt.Created, 1, LearningCreationReceipt.FingerprintFor(request), card, null)); return id;
        }
        var creation = AddCard(original);
        while (s.Learning.Count < count) {
            var id = Guid.NewGuid(); var card = new LearningItem(id, LearningDomain.French, "Synthetic unrelated card", "Synthetic answer", 2, 5, At, At);
            AddCard(card);
        }
        foreach (var card in s.Learning.Values.Where(x => x.Id != question.Id).Take(count - 3)) {
            var id = Guid.NewGuid(); s.Reviews.Add(id, new(id, new(Guid.NewGuid(), card.Id, ReviewRating.Again, TimeSpan.FromSeconds(2), At), card));
        }
        TrainingAttempt Attempt(Guid id, Guid q, long sequence, DateTimeOffset at, double? score = 3) {
            var assessment = new TrainingAssessment(Guid.NewGuid(), 1, 1, "self", "fixture-v1", [], score, "Europe/London", at);
            return new(id, session, q, 1, TrainingMode.RapidDrill, sequence, 2, false, at,
                [new(1, "Synthetic assessed answer", "text_only", at)], [assessment], assessment.Id);
        }
        var corrections = new Guid[iterations]; var deletions = new Guid[iterations];
        for (var i = 0; i < iterations; i++) {
            corrections[i] = Guid.NewGuid(); deletions[i] = Guid.NewGuid();
            s.TrainingAttempts[corrections[i]] = Attempt(corrections[i], question.Id, i * 2 + 1, At, 0);
            s.TrainingAttempts[deletions[i]] = Attempt(deletions[i], question.Id, i * 2 + 2, At);
        }
        // Later original practice and deliberately ineligible earlier assessments expose replay mistakes.
        var fixedId = Guid.NewGuid(); s.TrainingAttempts[fixedId] = Attempt(fixedId, question.Id, iterations * 2 + 1, At.AddHours(1), 4);
        var stale = Guid.NewGuid(); s.TrainingAttempts[stale] = Attempt(stale, question.Id, iterations * 2 + 2, At.AddHours(2), 0) with {
            Answers = [new(1, "Old synthetic answer", "text_only", At), new(2, "Already corrected synthetic answer", "user_corrected", At)] };
        var deleted = Guid.NewGuid(); s.TrainingAttempts[deleted] = Attempt(deleted, question.Id, iterations * 2 + 3, At.AddHours(3), 0) with { DeletedAt = At, Answers = [], Assessments = [], CanonicalAssessmentId = null };
        var skipped = Guid.NewGuid(); s.TrainingAttempts[skipped] = Attempt(skipped, question.Id, iterations * 2 + 4, At.AddHours(4), 0) with { Skipped = true, Answers = [], Assessments = [], CanonicalAssessmentId = null };
        var unknown = Guid.NewGuid(); s.TrainingAttempts[unknown] = Attempt(unknown, question.Id, iterations * 2 + 5, At.AddHours(5), null);
        while (s.TrainingAttempts.Count < count) {
            var id = Guid.NewGuid(); s.TrainingAttempts[id] = Attempt(id, QuestionBank.All[1].Id, s.TrainingAttempts.Count + 1, At);
        }
        for (var i = 0; i < 200; i++) {
            var id = Guid.NewGuid(); s.Goals[id] = new(id, 1, "Synthetic goal", "Synthetic brief", "Daily", false, At, At.AddMonths(1), "active", At, At);
        }
        TrainingProjection.Rebuild(s, question.Id);
        return new(s, question.Id, session, creation, lateReview, corrections, deletions);
    }
    internal Task Seed(IRuntimeStore store) => store.UpdateAsync(s => {
        s.Learning = State.Learning; s.Reviews = State.Reviews; s.CreationReceipts = State.CreationReceipts;
        s.TrainingBaselines = State.TrainingBaselines; s.TrainingBaselineReviews = State.TrainingBaselineReviews;
        s.TrainingManualSequences = State.TrainingManualSequences; s.NextTrainingSequence = State.NextTrainingSequence;
        s.TrainingAttempts = State.TrainingAttempts; s.Goals = State.Goals; return true;
    });
    internal Task SeedSession(ISessionStore sessions) => sessions.AddAsync(new(Session, ActivityType.General, "Synthetic mutation benchmark",
        SessionStatus.Active, null, null, At, At, []));
    internal async Task<object> Exercise(IRuntimeStore store, TrainingService service, int warmup, StoragePhaseCapture? phases = null)
    {
        var creationJson = JsonSerializer.Serialize(State.CreationReceipts[CreationRequest]);
        var corrections = new List<double>(); var deletions = new List<double>(); var rebuilds = new List<double>(); var pure = new List<double>();
        var coldHydration = new List<double>(); var learningHydration = new List<double>(); var trainingHydration = new List<double>();
        var requests = Corrections.Select(id => new CorrectTrainingAnswerRequest(Guid.NewGuid(), 2, "Corrected synthetic answer " + id)).ToArray();
        for (var i = 0; i < Corrections.Length; i++) {
            var timer = new Stopwatch(); TrainingAttempt corrected; double correctionMs;
            using (phases?.Begin("correction", i >= warmup)) {
                timer.Start(); corrected = await service.CorrectAsync(Corrections[i], requests[i], default); correctionMs = timer.Elapsed.TotalMilliseconds;
            }
            Assert.Equal(3, corrected.Revision); Assert.Equal(2, corrected.Answers.Count); Assert.Null(corrected.CanonicalAssessment);
            bool deleted; double deletionMs;
            using (phases?.Begin("deletion", i >= warmup)) {
                timer.Restart(); deleted = await service.DeleteAsync(Deletions[i], 2, default); deletionMs = timer.Elapsed.TotalMilliseconds;
            }
            Assert.True(deleted);
            double pureMs = 0, coldMs = 0, learningMs = 0, trainingMs = 0, rebuildMs;
            using (phases?.Begin("rebuild", i >= warmup)) {
                timer.Restart();
                await store.UpdateAsync(s => {
                    // Unit hydration stays inside the full transaction timer and outside pure CPU time.
                    var hydration = Stopwatch.StartNew(); s.LoadColdProperties(); coldMs = hydration.Elapsed.TotalMilliseconds;
                    hydration.Restart(); s.LoadLearningProperties(); learningMs = hydration.Elapsed.TotalMilliseconds;
                    hydration.Restart(); s.LoadTrainingProperties(); trainingMs = hydration.Elapsed.TotalMilliseconds;
                    var projection = Stopwatch.StartNew(); TrainingProjection.Rebuild(s, Question); pureMs = projection.Elapsed.TotalMilliseconds; return true;
                });
                rebuildMs = timer.Elapsed.TotalMilliseconds;
            }
            var card = await store.ReadLearningItemAsync(Question); Assert.NotNull(card);
            Assert.Equal(4d, card.MasteryScore); Assert.Equal(i == Corrections.Length - 1 ? 1 : 2, card.StrongStreak); Assert.Equal(7, card.Difficulty);
            Assert.Equal(i == Corrections.Length - 1 ? 17.5 : 35, card.Stability);
            Assert.Equal(At.AddDays(1).AddDays(card.Stability), card.DueAt);
            if (i >= warmup) { corrections.Add(correctionMs); deletions.Add(deletionMs); rebuilds.Add(rebuildMs); pure.Add(pureMs); coldHydration.Add(coldMs); learningHydration.Add(learningMs); trainingHydration.Add(trainingMs); }
            if ((i + 1) % 10 == 0) Console.WriteLine($"Mutation dataset {State.Learning.Count}: {i + 1}/{Corrections.Length} real correction/delete pairs complete.");
        }
        Assert.Equal(3, (await service.CorrectAsync(Corrections[0], requests[0], default)).Revision);
        Assert.Equal(409, (await Assert.ThrowsAsync<DomainException>(() => service.CorrectAsync(Corrections[0], requests[0] with { Text = "Different replay" }, default))).StatusCode);
        Assert.False(await service.DeleteAsync(Deletions[0], 2, default));
        var final = await store.ReadAsync(s => {
            Assert.Equal(creationJson, JsonSerializer.Serialize(s.CreationReceipts[CreationRequest]));
            Assert.All(Deletions, id => { Assert.Empty(s.TrainingAttempts[id].Answers); Assert.Empty(s.TrainingAttempts[id].Assessments); Assert.NotNull(s.TrainingAttempts[id].DeletedAt); });
            Assert.All(Corrections, id => Assert.Null(s.TrainingAttempts[id].CanonicalAssessment));
            return new { cards = s.Learning.Count, reviews = s.Reviews.Count, creationReceipts = s.CreationReceipts.Count, trainingAttempts = s.TrainingAttempts.Count,
                correctionOperations = s.TrainingOperations.Count, goals = s.Goals.Count, nextSequence = s.NextTrainingSequence,
                question = Question, session = Session, sampleCorrection = Corrections[0], sampleDeletion = Deletions[0], originalCreationRequest = CreationRequest };
        });
        Assert.Equal(State.Learning.Count, final.cards); Assert.Equal(State.Reviews.Count, final.reviews); Assert.Equal(State.CreationReceipts.Count, final.creationReceipts);
        Assert.Equal(State.TrainingAttempts.Count, final.trainingAttempts); Assert.Equal(Corrections.Length, final.correctionOperations); Assert.Equal(State.NextTrainingSequence, final.nextSequence);
        object Stats(List<double> values) => new { p50Ms = values.Order().ElementAt((int)Math.Ceiling(values.Count * .5) - 1),
            p95Ms = values.Order().ElementAt((int)Math.Ceiling(values.Count * .95) - 1), maxMs = values.Max(), samplesMs = values };
        var phaseTimings = phases?.Report();
        if (phaseTimings is not null) foreach (var lane in new[] { "correction", "deletion", "rebuild" }) {
            if (lane == "correction") {
                foreach (var phase in new[] { "correction.context", "correction.begin", "correction.privacy_gate", "correction.lock", "correction.target", "correction.decision",
                    "correction.learning.baseline", "correction.training.inputs", "correction.learning.history", "correction.project", "correction.save", "correction.commit" })
                    Assert.Equal(corrections.Count, phaseTimings[lane][phase].Count);
                foreach (var phase in new[] { "runtime.cold.hydrate", "runtime.cold.serialize_hash", "runtime.training.hydrate", "runtime.learning.hydrate" })
                    Assert.DoesNotContain(phase, phaseTimings[lane].Keys);
                continue;
            }
            foreach (var phase in new[] { "runtime.update.callback", "runtime.update.serialize_after", "runtime.update.save",
                "runtime.learning.hydrate", "runtime.learning.diff", "runtime.training.hydrate", "runtime.training.diff" })
                Assert.Equal(corrections.Count, phaseTimings[lane][phase].Count);
            foreach (var phase in new[] { "runtime.cold.hydrate", "runtime.cold.serialize_hash" })
                Assert.Equal(corrections.Count, phaseTimings[lane][phase].Count);
        }
        return new { initialCards = State.Learning.Count, initialReviews = State.Reviews.Count, initialCreationReceipts = State.CreationReceipts.Count,
            initialTrainingAttempts = State.TrainingAttempts.Count, unrelatedTrainingAttempts = State.TrainingAttempts.Count - (Corrections.Length * 2 + 5),
            warmup, samples = corrections.Count, correction = Stats(corrections), deletion = Stats(deletions), cohesiveIdempotentRebuild = Stats(rebuilds), pureRebuild = Stats(pure),
            explicitColdHydration = Stats(coldHydration), explicitLearningHydration = Stats(learningHydration), explicitTrainingHydration = Stats(trainingHydration),
            rebuildDiagnosticForcesColdHydration = true, phaseTimings, final };
    }
}

public sealed class LearningMutationFixtureTests
{
    [Fact]
    public async Task Mutation_fixture_preserves_replay_baseline_exclusion_later_practice_and_deletion_content() {
        var fixture = LearningMutationFixture.Make(32, 6); IRuntimeStore store = new InMemoryRuntimeStore(); await fixture.Seed(store);
        var sessions = new InMemorySessionStore(); await fixture.SeedSession(sessions);
        var service = new TrainingService(store, sessions, new ForgeClock { Now = LearningMutationFixture.At.AddDays(20) });
        using var epoch = PrivacyContext.Enter(0); await fixture.Exercise(store, service, 1);
        // Remove the late manual review to check original-practice London calendar scheduling across DST.
        await store.UpdateAsync(s => { s.Reviews.Remove(fixture.LateReview); TrainingProjection.Rebuild(s, fixture.Question); return true; });
        var card = await store.ReadLearningItemAsync(fixture.Question); Assert.NotNull(card); Assert.Equal(7, card.Stability);
        Assert.Equal(DateTimeOffset.Parse("2026-04-04T12:00:00Z"), card.DueAt); //13:00 on28Mar becomes13:00 BST on4Apr.
    }
}

public sealed partial class PostgresSessionTests
{
    [RuntimeMeasurementFact]
    public async Task Measure_training_correction_deletion_and_rebuild_with_unrelated_learning_state() {
        var reports = new List<object>();
        foreach (var count in new[] { 1_000, 2_000, 10_000 }) {
            await using var database = await TestDatabase.CreateAsync();
            var connection = new NpgsqlConnectionStringBuilder(database.ConnectionString) { Pooling = true }.ConnectionString;
            try {
                using var root = new PostgresApiFactory(connection, manualAgents: true);
                using var host = root.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.RemoveAll<IHostedService>()));
                var store = host.Services.GetRequiredService<IRuntimeStore>(); var fixture = LearningMutationFixture.Make(count, 55);
                using var epoch = PrivacyContext.Enter(0); await fixture.SeedSession(host.Services.GetRequiredService<ISessionStore>()); await fixture.Seed(store);
                var service = new TrainingService(store, host.Services.GetRequiredService<ISessionReader>(), new ForgeClock { Now = LearningMutationFixture.At.AddDays(20) });
                using var phases = new StoragePhaseCapture(); reports.Add(await fixture.Exercise(store, service, 5, phases));
                // Persist completed sizes incrementally, retaining evidence if a later size fails.
                await File.WriteAllTextAsync(Environment.GetEnvironmentVariable("JARVIS_PERF_OUTPUT")!, JsonSerializer.Serialize(new {
                    generatedAt = DateTimeOffset.UtcNow, synthetic = true, pooled = true, backgroundWorkers = false, unrelatedGoals = 200,
                    operation = "Distinct correction and deletion per sample; no setup/reset in timers. Rebuild diagnostic explicitly loads cold, learning and training units before pure projection; pure timer excludes all storage. Counts include retained training tombstones.",
                    targetMs = 50, targetIsUnchanged = true, optimizationImplemented = true, optimization = "Canonical training extraction and indexed correction; deletion remains cohesive",
                    instrumentationOverheadIncluded = true, nestedPhasesOverlapOuterPhases = true, reports
                }, new JsonSerializerOptions { WriteIndented = true }));
            } finally { using var pool = new NpgsqlConnection(connection); NpgsqlConnection.ClearPool(pool); }
        }
    }
}
