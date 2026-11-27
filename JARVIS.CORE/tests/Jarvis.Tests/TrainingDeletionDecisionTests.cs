using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Jarvis.Orchestrator.Training;

namespace Jarvis.Tests;

public sealed class TrainingDeletionDecisionTests
{
    [Theory]
    [InlineData("missing")] [InlineData("deleted")] [InlineData("stale")] [InlineData("zero")]
    [InlineData("identity")] [InlineData("overflow")]
    public void Noop_and_refusal_rules_preserve_current_state(string scenario)
    {
        var id = Guid.NewGuid(); TrainingAttempt? current = Make(id); var revision = current.Revision;
        if (scenario == "missing") current = null;
        if (scenario == "deleted") { current = current! with { DeletedAt = TrainingMigrationFixture.At }; revision = 0; }
        if (scenario == "stale") revision++;
        if (scenario == "zero") revision = 0;
        if (scenario == "identity") current = current! with { Id = Guid.NewGuid() };
        if (scenario == "overflow") { current = current! with { Revision = int.MaxValue }; revision = int.MaxValue; }
        var before = JsonSerializer.Serialize(current);
        if (scenario is "missing" or "deleted") Assert.Null(TrainingDeletion.Decide(id, revision, current, TrainingMigrationFixture.At));
        else if (scenario == "identity") Assert.Throws<InvalidDataException>(() => TrainingDeletion.Decide(id, revision, current, TrainingMigrationFixture.At));
        else if (scenario == "overflow") Assert.Throws<OverflowException>(() => TrainingDeletion.Decide(id, revision, current, TrainingMigrationFixture.At));
        else Assert.Equal(409, Assert.Throws<DomainException>(() => TrainingDeletion.Decide(id, revision, current, TrainingMigrationFixture.At)).StatusCode);
        Assert.Equal(before, JsonSerializer.Serialize(current));
    }

    [Fact]
    public void Tombstone_clears_content_but_keeps_source_and_practice_metadata()
    {
        var id = Guid.NewGuid(); var original = Make(id); var at = TrainingMigrationFixture.At.AddMinutes(1);
        var changed = TrainingDeletion.Decide(id, 2, original, at)!;
        Assert.Equal(original with { Revision = 3, DeletedAt = at, Answers = changed.Answers, Assessments = changed.Assessments, CanonicalAssessmentId = null }, changed);
        Assert.Empty(changed.Answers); Assert.Empty(changed.Assessments); Assert.Null(changed.CanonicalAssessmentId);
        Assert.Single(original.Answers); Assert.Single(original.Assessments);
    }

    [Fact]
    public void Missing_baseline_still_retires_reasoning_without_resurrecting_projection_or_purging_receipts()
    {
        var id = Guid.NewGuid(); var other = Guid.NewGuid(); var state = new RuntimeState(); state.TrainingAttempts[id] = Make(id);
        SeedReasoning(state, id, other); var receipt = new TrainingOperation("submit", id, "existing-receipt"); state.TrainingOperations[id] = receipt;
        Assert.True(TrainingDeletion.Apply(state, id, 2, new ForgeClock { Now = TrainingMigrationFixture.At }));
        Assert.Empty(state.Learning); Assert.Empty(state.TrainingBaselines); Assert.Equal(1, state.NextTrainingSequence);
        Assert.Same(receipt, state.TrainingOperations[id]); Assert.Contains(id, state.RetiredReasoningScopes);
        Assert.DoesNotContain(id, state.ProviderResults.Keys); Assert.Contains(other, state.ProviderResults.Keys);
        Assert.True(state.ProviderAttempts[id].SourceDeleted); Assert.Equal("", state.ProviderAttempts[id].Fingerprint);
        Assert.Equal("unrelated-fingerprint", state.ProviderAttempts[other].Fingerprint);
        Assert.False(TrainingDeletion.Apply(state, id, 0, new ForgeClock { Now = TrainingMigrationFixture.At }));
    }

    [Fact]
    public void Projection_failure_does_not_tombstone_or_retire_source()
    {
        var id = Guid.NewGuid(); var state = new RuntimeState(); var attempt = Make(id); state.TrainingAttempts[id] = attempt;
        state.TrainingBaselines[attempt.QuestionId] = new(Guid.NewGuid(), LearningDomain.CSharpDotNet, "Wrong identity", "Reference", 1, 5, TrainingMigrationFixture.At, TrainingMigrationFixture.At);
        Assert.Throws<DomainException>(() => TrainingDeletion.Apply(state, id, 2, new ForgeClock { Now = TrainingMigrationFixture.At }));
        Assert.Same(attempt, state.TrainingAttempts[id]); Assert.Empty(state.RetiredReasoningScopes);
    }

    [Fact]
    public async Task Service_dispatches_to_deletion_port_without_broad_callback()
    {
        var store = new DeletionPortSpy(); var id = Guid.NewGuid();
        Assert.True(await new TrainingService(store, new InMemorySessionStore(), TimeProvider.System).DeleteAsync(id, 3, default));
        Assert.Equal((id, 3), store.Call);
    }

    internal static void SeedReasoning(RuntimeState state, Guid scope, Guid other)
    {
        var route = new ModelRoute("synthetic", "fixture-model", "fixture-profile", "fixture-policy", "fixture-rate", new(2026, 11, 1), 1, 2, 50);
        foreach (var id in new[] { scope, other }) {
            state.ProviderAttempts[id] = new(id, id, id == scope ? "private-fingerprint" : "unrelated-fingerprint", route, Guid.NewGuid(), "Completed", TrainingMigrationFixture.At, TrainingMigrationFixture.At);
            state.ProviderResults[id] = new(id, id, "private-cache", "{\"synthetic\":true}", TrainingMigrationFixture.At);
        }
    }
    private static TrainingAttempt Make(Guid id) => new(id, Guid.NewGuid(), QuestionBank.All[0].Id, 1,
        TrainingMode.RapidDrill, 19, 2, false, TrainingMigrationFixture.At,
        [new(1, "Synthetic answer", "text_only", TrainingMigrationFixture.At)],
        [new(id, 1, 1, "self", "fixture-v1", [], 3, "Europe/London", TrainingMigrationFixture.At)], id,
        DrillId: Guid.NewGuid(), StepIndex: 0);
    private sealed class DeletionPortSpy : IRuntimeStore
    {
        public (Guid, int) Call { get; private set; }
        public Task<bool> DeleteTrainingAttemptAsync(Guid id, int expectedRevision, TimeProvider clock, CancellationToken ct = default) { Call = (id, expectedRevision); return Task.FromResult(true); }
        public Task<T> ReadAsync<T>(Func<RuntimeState, T> read, CancellationToken ct = default) => throw new InvalidOperationException("Unexpected broad read");
        public Task<T> UpdateAsync<T>(Func<RuntimeState, T> update, CancellationToken ct = default) => throw new InvalidOperationException("Unexpected broad write");
    }
}
