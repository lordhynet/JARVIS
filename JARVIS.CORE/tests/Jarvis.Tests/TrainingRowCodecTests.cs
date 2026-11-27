using System.Text.Json;
using System.Text.Json.Nodes;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Jarvis.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Jarvis.Tests;

public sealed class TrainingRowCodecTests
{
    private static TrainingAttempt Attempt(Guid id) => new(id, Guid.NewGuid(), Guid.NewGuid(), 1, TrainingMode.RapidDrill,
        7, 1, false, TrainingMigrationFixture.At, [new(1, "Synthetic original", "user", TrainingMigrationFixture.At)], []);
    private static RuntimeTrainingRow Row(string kind, Guid id, object value) => new() { Kind = kind, Id = id, Json = JsonSerializer.Serialize(value) };
    private static List<RuntimeTrainingRow> Apply(IEnumerable<RuntimeTrainingRow> rows, RuntimeTrainingChanges changes)
    {
        var result = rows.ToDictionary(r => new RuntimeTrainingKey(r.Kind, r.Id), r => r);
        foreach (var key in changes.Removed) Assert.True(result.Remove(key));
        foreach (var row in changes.Upserts) result[new(row.Kind, row.Id)] = row;
        return result.Values.ToList();
    }

    [Fact]
    public async Task Real_training_fixture_survives_codec_restore_replay_purges_and_post_restore_edits()
    {
        var store = new InMemoryRuntimeStore(); var sessions = new InMemorySessionStore(); var fixture = new TrainingMigrationFixture();
        await fixture.Seed(store, sessions); var expected = await TrainingMigrationFixture.Snapshot(store);
        var state = await store.ReadAsync(s => s);
        var rows = RuntimeTrainingSnapshot.Read([]).Prepare(state.TrainingAttempts, state.TrainingOperations).Upserts;
        var snapshot = RuntimeTrainingSnapshot.Read(rows);
        state.TrainingAttempts = snapshot.TrainingAttempts.ToDictionary(); state.TrainingOperations = snapshot.TrainingOperations.ToDictionary();
        store.ReplacePrivacyData(state);
        TrainingMigrationFixture.Same(expected, await TrainingMigrationFixture.Snapshot(store));
        await fixture.Replay(store, sessions);
        state = await store.ReadAsync(s => s);
        var noOp = snapshot.Prepare(state.TrainingAttempts, state.TrainingOperations);
        Assert.Empty(noOp.Upserts); Assert.Empty(noOp.Removed);
        Assert.Null(state.TrainingOperations[fixture.NullOperation].Fingerprint);
        Assert.Equal(TrainingMigrationFixture.At.Offset, state.TrainingAttempts[fixture.Submit.RequestId].SubmittedAt.Offset);

        await fixture.EditAfterRestore(store, sessions); state = await store.ReadAsync(s => s);
        var changes = snapshot.Prepare(state.TrainingAttempts, state.TrainingOperations);
        Assert.Equal(2, changes.Upserts.Count); Assert.Empty(changes.Removed);
        var restored = RuntimeTrainingSnapshot.Read(Apply(rows, changes));
        TrainingMigrationFixture.Same(JsonSerializer.Serialize(state.TrainingAttempts), JsonSerializer.Serialize(restored.TrainingAttempts));
        TrainingMigrationFixture.Same(JsonSerializer.Serialize(state.TrainingOperations), JsonSerializer.Serialize(restored.TrainingOperations));
        Assert.Equal("Synthetic unrelated goal", state.Goals[fixture.Goal].Title);
        Assert.Equal(104, state.NextTrainingSequence);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("existing-fingerprint")]
    public void Request_identity_and_original_operation_kind_and_purge_marker_survive_without_a_live_target(string? fingerprint)
    {
        var request = Guid.NewGuid(); var target = Guid.NewGuid();
        var snapshot = RuntimeTrainingSnapshot.Read([Row("TrainingOperations", request, new TrainingOperation("assessment:ai:existing-model", target, fingerprint!))]);
        var operation = Assert.Single(snapshot.TrainingOperations);
        Assert.Equal(request, operation.Key); Assert.Equal(target, operation.Value.AttemptId);
        Assert.Equal(fingerprint, operation.Value.Fingerprint); Assert.Equal("assessment:ai:existing-model", operation.Value.Kind);
        Assert.Empty(snapshot.Prepare(snapshot.TrainingAttempts, snapshot.TrainingOperations).Upserts);
    }

    [Fact]
    public void Shared_uuid_in_distinct_kinds_does_not_alias_and_complete_empty_after_image_removes_both()
    {
        var id = Guid.NewGuid(); var attempt = Attempt(id);
        var rows = new[] { Row("TrainingAttempts", id, attempt), Row("TrainingOperations", id, new TrainingOperation("submit", id, "fp")) };
        var snapshot = RuntimeTrainingSnapshot.Read(rows);
        var operations = snapshot.TrainingOperations.ToDictionary(); operations[id] = operations[id] with { Fingerprint = "" };
        var changed = snapshot.Prepare(snapshot.TrainingAttempts, operations);
        Assert.Equal("TrainingOperations", Assert.Single(changed.Upserts).Kind); Assert.Empty(changed.Removed);
        Assert.Equal(rows[0].Json, Apply(rows, changed).Single(r => r.Kind == "TrainingAttempts").Json);
        var erased = snapshot.Prepare(new Dictionary<Guid, TrainingAttempt>(), new Dictionary<Guid, TrainingOperation>());
        Assert.Empty(erased.Upserts); Assert.Equal(2, erased.Removed.Count); Assert.Empty(Apply(rows, erased));
    }

    [Fact]
    public void Untouched_raw_json_keeps_unknown_fields_and_formatting_while_a_neighbor_changes()
    {
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var json = JsonNode.Parse(JsonSerializer.Serialize(Attempt(first)))!; json["FutureMetadata"] = "retain synthetic sentinel";
        var raw = new RuntimeTrainingRow { Kind = "TrainingAttempts", Id = first, Json = json.ToJsonString(new() { WriteIndented = true }) };
        var neighbor = Row("TrainingAttempts", second, Attempt(second)); var rows = new[] { raw, neighbor };
        var snapshot = RuntimeTrainingSnapshot.Read(rows); var attempts = snapshot.TrainingAttempts.ToDictionary();
        attempts[second] = attempts[second] with { Revision = 2 };
        var changes = snapshot.Prepare(attempts, snapshot.TrainingOperations);
        Assert.Equal(second, Assert.Single(changes.Upserts).Id);
        Assert.Equal(raw.Json, Apply(rows, changes).Single(r => r.Id == first).Json);
    }

    [Fact]
    public void Nested_mutation_cannot_change_the_captured_before_image()
    {
        var id = Guid.NewGuid(); var row = Row("TrainingAttempts", id, Attempt(id)); var snapshot = RuntimeTrainingSnapshot.Read([row]);
        var answers = Assert.IsAssignableFrom<IList<TrainingAnswerVersion>>(snapshot.TrainingAttempts[id].Answers);
        answers[0] = answers[0] with { Text = "Synthetic nested edit" };
        var changes = snapshot.Prepare(snapshot.TrainingAttempts, snapshot.TrainingOperations);
        Assert.Contains("Synthetic nested edit", Assert.Single(changes.Upserts).Json);
        Assert.DoesNotContain("Synthetic nested edit", row.Json);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Offset_only_provenance_changes_are_not_hidden_by_equal_instant_record_equality(bool nested)
    {
        var id = Guid.NewGuid(); var snapshot = RuntimeTrainingSnapshot.Read([Row("TrainingAttempts", id, Attempt(id))]);
        var original = snapshot.TrainingAttempts[id]; var offset = TimeSpan.FromHours(-4);
        var next = nested ? original with { Answers = [original.Answers[0] with { At = original.Answers[0].At.ToOffset(offset) }] }
            : original with { SubmittedAt = original.SubmittedAt.ToOffset(offset) };
        Assert.Equal(original.SubmittedAt, next.SubmittedAt);
        var attempts = snapshot.TrainingAttempts.ToDictionary(); attempts[id] = next;
        var changed = Assert.Single(snapshot.Prepare(attempts, snapshot.TrainingOperations).Upserts);
        var decoded = RuntimeTrainingSnapshot.Read([changed]).TrainingAttempts[id];
        Assert.Equal(offset, nested ? decoded.Answers[0].At.Offset : decoded.SubmittedAt.Offset);
        Assert.Equal(original.SubmittedAt.UtcTicks, decoded.SubmittedAt.UtcTicks);
    }

    [Fact]
    public void Unknown_duplicate_malformed_null_or_mismatched_rows_fail_closed_without_echoing_payloads()
    {
        var id = Guid.NewGuid(); var valid = Row("TrainingAttempts", id, Attempt(id));
        RuntimeTrainingRow[] bad = [new() { Kind = "FutureKind", Id = id, Json = valid.Json }, new() { Kind = valid.Kind, Id = Guid.Empty, Json = valid.Json },
            new() { Kind = valid.Kind, Id = Guid.NewGuid(), Json = valid.Json }, new() { Kind = valid.Kind, Id = id, Json = "null" },
            new() { Kind = valid.Kind, Id = id, Json = "{\"Id\":\"SYNTHETIC_PRIVATE_MARKER\"}" },
            new() { Kind = "TrainingOperations", Id = id, Json = "{}" }, new() { Kind = valid.Kind, Id = id, Json = "[]" }];
        foreach (var row in bad) Assert.Equal("Invalid canonical training row.", Assert.Throws<InvalidDataException>(() => RuntimeTrainingSnapshot.Read([valid, row])).Message);
        Assert.Throws<InvalidDataException>(() => RuntimeTrainingSnapshot.Read([valid, valid]));
        Assert.Throws<InvalidDataException>(() => RuntimeTrainingSnapshot.Read([null!]));
    }

    [Fact]
    public void Invalid_after_image_produces_no_partial_plan_and_does_not_corrupt_the_snapshot()
    {
        var id = Guid.NewGuid(); var row = Row("TrainingAttempts", id, Attempt(id)); var snapshot = RuntimeTrainingSnapshot.Read([row]);
        var attempts = snapshot.TrainingAttempts.ToDictionary(); attempts[id] = attempts[id] with { Revision = 2 };
        Assert.Throws<InvalidDataException>(() => snapshot.Prepare(attempts, new Dictionary<Guid, TrainingOperation> { [Guid.Empty] = new("submit", id, "fp") }));
        Assert.Empty(snapshot.Prepare(snapshot.TrainingAttempts, snapshot.TrainingOperations).Upserts);
        Assert.Throws<ArgumentNullException>(() => snapshot.Prepare(null!, snapshot.TrainingOperations));
        Assert.Throws<ArgumentNullException>(() => snapshot.Prepare(snapshot.TrainingAttempts, null!));
    }

    [Fact]
    public void Canonical_training_type_is_distinct_and_has_only_opaque_key_and_payload_mapping()
    {
        using var context = new SessionDbContext(new DbContextOptionsBuilder<SessionDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=unused_codec_model;Username=unused").Options);
        Assert.NotEqual(typeof(RuntimeLearningRow), typeof(RuntimeTrainingRow));
        var entity = context.Model.FindEntityType(typeof(RuntimeTrainingRow)); Assert.NotNull(entity);
        Assert.Equal("RuntimeTrainingRows", entity.GetTableName());
        Assert.Equal(new[] { "Kind", "Id" }, entity.FindPrimaryKey()!.Properties.Select(p => p.Name));
        Assert.Equal("jsonb", entity.FindProperty("Json")!.GetColumnType());
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public void Training_loader_retries_failed_hydration_closes_unused_loaders_and_preserves_default_serialization()
    {
        var state = new RuntimeState(); var calls = 0; var id = Guid.NewGuid();
        state.DeferColdProperties(() => throw new InvalidOperationException("Cold must stay lazy."));
        state.DeferTrainingProperties(() => { if (++calls == 1) throw new InvalidDataException("Synthetic failed load"); state.TrainingAttempts[id] = Attempt(id); });
        Assert.Throws<InvalidDataException>(() => state.TrainingAttempts.Count); Assert.False(state.AreTrainingPropertiesLoaded);
        Assert.Single(state.TrainingAttempts); Assert.Equal(2, calls); Assert.False(state.AreColdPropertiesLoaded);
        var closed = new RuntimeState(); closed.DeferTrainingProperties(() => { }); closed.CloseTrainingProperties();
        Assert.Throws<InvalidOperationException>(() => closed.TrainingOperations.Count);
        var normal = new RuntimeState(); normal.TrainingAttempts[id] = Attempt(id);
        Assert.Single(JsonSerializer.Deserialize<RuntimeState>(JsonSerializer.Serialize(normal))!.TrainingAttempts);
    }
}
