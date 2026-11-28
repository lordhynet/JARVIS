using Jarvis.Contracts;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Training;
using Jarvis.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

// Shared generated-question store port: unit writer rules, in-memory atomicity, erasure. No generated application path exists.
public sealed class GeneratedQuestionStorePortTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-28T12:00:00Z");
    private static string Hex => new('a', 64);
    internal static GeneratedQuestionWrite Write(Guid session, Guid operation, string prompt = "How would you shard a write-heavy table?",
        IReadOnlyList<string>? followUps = null, DateTimeOffset? at = null) =>
        new(session, operation, LearningDomain.SystemDesign, "scaling", 1, Hex, prompt, "Partition keys, hot spots and rebalancing.",
            followUps ?? ["What if one shard is hot?"], """{"source":"ai-realtime"}""", "[]", at ?? At);
    private static GeneratedQuestionScope Scope(Guid session, params Guid[] operations) => new(session, operations, []);

    // ---- Unit writer-boundary rules ----

    [Fact]
    public void The_question_ID_is_derived_by_Core_and_never_chosen_by_the_caller()
    {
        var session = Guid.NewGuid(); var op = Guid.NewGuid();
        var unit = new GeneratedQuestionUnit(Scope(session, op), [], [], 0);
        Assert.Equal(GeneratedQuestionIds.For(session, op), unit.Add(Write(session, op)));
        Assert.NotEqual(GeneratedQuestionIds.For(session, op), GeneratedQuestionIds.For(Guid.NewGuid(), op));
        Assert.DoesNotContain(QuestionBank.All, q => q.Id == GeneratedQuestionIds.For(session, op));
    }

    public static TheoryData<string, Func<Guid, Guid, GeneratedQuestionWrite>> OutOfBounds => new()
    {
        { "empty prompt", (s, o) => Write(s, o, prompt: "") },
        { "blank prompt", (s, o) => Write(s, o, prompt: "   ") },
        { "long prompt", (s, o) => Write(s, o, prompt: new string('x', GeneratedQuestionUnit.MaximumPrompt + 1)) },
        { "three follow-ups", (s, o) => Write(s, o, followUps: ["a", "b", "c"]) },
        { "long follow-up", (s, o) => Write(s, o, followUps: [new string('x', GeneratedQuestionUnit.MaximumFollowUp + 1)]) },
        { "unsupported topic", (s, o) => Write(s, o) with { Topic = LearningDomain.French } },
        { "missing outline", (s, o) => Write(s, o) with { OutlineKey = "" } },
        { "short fingerprint", (s, o) => Write(s, o) with { Fingerprint = "abc" } },
        { "missing provenance", (s, o) => Write(s, o) with { ProvenanceJson = "" } }
    };

    [Theory]
    [MemberData(nameof(OutOfBounds))]
    public void Out_of_bounds_writes_are_refused(string _, Func<Guid, Guid, GeneratedQuestionWrite> write)
    {
        var session = Guid.NewGuid(); var op = Guid.NewGuid();
        Assert.Equal(400, Assert.Throws<DomainException>(() => new GeneratedQuestionUnit(Scope(session, op), [], [], 0).Add(write(session, op))).StatusCode);
    }

    [Fact]
    public void Writes_outside_the_scope_or_duplicated_are_refused()
    {
        var session = Guid.NewGuid(); var op = Guid.NewGuid();
        var unit = new GeneratedQuestionUnit(Scope(session, op), [], [], 0);
        Assert.Equal(409, Assert.Throws<DomainException>(() => unit.Add(Write(Guid.NewGuid(), op))).StatusCode);
        Assert.Equal(409, Assert.Throws<DomainException>(() => unit.Add(Write(session, Guid.NewGuid()))).StatusCode);
        unit.Add(Write(session, op));
        Assert.Equal(409, Assert.Throws<DomainException>(() => unit.Add(Write(session, op))).StatusCode);
        Assert.Equal(409, Assert.Throws<DomainException>(() => unit.Purge([Guid.NewGuid()], At)).StatusCode);
    }

    [Fact]
    public void Session_and_total_quotas_are_enforced_in_the_unit_and_hourly_counts_include_pending_adds()
    {
        var session = Guid.NewGuid(); var op = Guid.NewGuid();
        var full = Enumerable.Repeat(At, GeneratedQuestionUnit.MaximumPerSession).ToArray();
        Assert.Equal(409, Assert.Throws<DomainException>(() => new GeneratedQuestionUnit(Scope(session, op), [], full, 0).Add(Write(session, op))).StatusCode);
        Assert.Equal(409, Assert.Throws<DomainException>(() =>
            new GeneratedQuestionUnit(Scope(session, op), [], [], GeneratedQuestionUnit.MaximumTotal).Add(Write(session, op))).StatusCode);
        var unit = new GeneratedQuestionUnit(Scope(session, op), [], [At.AddHours(-2), At.AddMinutes(-10)], 2);
        unit.Add(Write(session, op));
        Assert.Equal(2, unit.SessionCountSince(At.AddHours(-1))); Assert.Equal(3, unit.SessionCount);
    }

    // ---- In-memory port atomicity ----

    [Fact]
    public async Task Rows_and_runtime_state_commit_together_and_are_found_by_operation_on_the_next_update()
    {
        var store = new InMemoryRuntimeStore(); var session = Guid.NewGuid(); var op = Guid.NewGuid(); var marker = Guid.NewGuid();
        var id = await store.UpdateWithGeneratedQuestionsAsync(Scope(session, op), (s, unit) => { s.RetiredSessions.Add(marker); return unit.Add(Write(session, op)); });
        Assert.Contains(marker, await store.ReadAsync(s => s.RetiredSessions));
        var found = await store.UpdateWithGeneratedQuestionsAsync(Scope(session, op), (_, unit) => unit.ByOperation(op));
        Assert.Equal(id, found!.Question.Id); Assert.Equal(Hex, found.Fingerprint); Assert.Equal("How would you shard a write-heavy table?", found.Question.Prompt);
    }

    [Fact]
    public async Task A_failed_callback_commits_neither_the_row_nor_the_state_change()
    {
        var store = new InMemoryRuntimeStore(); var session = Guid.NewGuid(); var op = Guid.NewGuid(); var marker = Guid.NewGuid();
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateWithGeneratedQuestionsAsync<bool>(Scope(session, op), (s, unit) =>
        {
            s.RetiredSessions.Add(marker); unit.Add(Write(session, op)); throw new InvalidOperationException("fence failed");
        }));
        Assert.DoesNotContain(marker, await store.ReadAsync(s => s.RetiredSessions));
        Assert.Empty(store.GeneratedQuestionsSnapshot());
    }

    [Fact]
    public async Task Purge_leaves_a_content_free_tombstone_with_no_fingerprint()
    {
        var store = new InMemoryRuntimeStore(); var session = Guid.NewGuid(); var op = Guid.NewGuid();
        var id = await store.UpdateWithGeneratedQuestionsAsync(Scope(session, op), (_, unit) => unit.Add(Write(session, op)));
        await store.UpdateWithGeneratedQuestionsAsync(new(session, [], [id]), (_, unit) => { unit.Purge([id], At); return true; });
        var row = Assert.Single(store.GeneratedQuestionsSnapshot());
        Assert.Equal(id, row.Question.Id); Assert.Null(row.Question.Prompt); Assert.Null(row.Question.ReferenceOutline);
        Assert.Empty(row.Question.FollowUps); Assert.Equal("", row.Fingerprint); Assert.Equal(At, row.Question.PurgedAt);
    }

    [Fact]
    public async Task A_row_added_and_purged_in_one_update_is_stored_only_as_a_tombstone()
    {
        var store = new InMemoryRuntimeStore(); var session = Guid.NewGuid(); var op = Guid.NewGuid();
        await store.UpdateWithGeneratedQuestionsAsync(Scope(session, op), (_, unit) => { var id = unit.Add(Write(session, op)); unit.Purge([id], At); return id; });
        Assert.Null(Assert.Single(store.GeneratedQuestionsSnapshot()).Question.Prompt);
    }

    [Fact]
    public async Task Erasure_deletes_generated_rows_and_new_work_after_reopen_starts_clean()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException("No provider call"));
        var store = host.Services.GetRequiredService<IRuntimeStore>(); var inner = host.Services.GetRequiredService<InMemoryRuntimeStore>();
        var session = Guid.NewGuid(); var op = Guid.NewGuid();
        await store.UpdateWithGeneratedQuestionsAsync(Scope(session, op), (_, unit) => unit.Add(Write(session, op)));
        Assert.Single(inner.GeneratedQuestionsSnapshot());
        var privacy = host.Services.GetRequiredService<IPrivacyStore>(); var status = await privacy.StatusAsync();
        var erased = await privacy.EraseAsync(new(Guid.NewGuid(), status.Revision, PrivacyData.ErasePhrase), new string('a', 64), DateTimeOffset.UtcNow);
        Assert.Empty(inner.GeneratedQuestionsSnapshot());
        var reopened = await privacy.ReopenAsync(new(erased.Revision, PrivacyData.ReopenPhrase), new string('b', 64));
        using (PrivacyContext.Enter(status.Epoch))   // old-epoch work is blocked
            await Assert.ThrowsAsync<DomainException>(() => store.UpdateWithGeneratedQuestionsAsync(Scope(session, op), (_, unit) => unit.Add(Write(session, op))));
        using (PrivacyContext.Enter(reopened.Epoch))  // valid new work starts from empty storage
            await store.UpdateWithGeneratedQuestionsAsync(Scope(session, op), (_, unit) => unit.Add(Write(session, op)));
        Assert.Single(inner.GeneratedQuestionsSnapshot());
    }

    [Fact]
    public async Task Composite_commits_and_retention_replacement_do_not_touch_generated_rows()
    {
        var store = new InMemoryRuntimeStore(); var session = Guid.NewGuid(); var op = Guid.NewGuid();
        await store.UpdateWithGeneratedQuestionsAsync(Scope(session, op), (_, unit) => unit.Add(Write(session, op)));
        store.ReplacePrivacyData(await store.ReadAsync(s => s));
        Assert.Single(store.GeneratedQuestionsSnapshot());
    }
}


// Review SG1/SG2: detached generated data, frozen writes, retained provenance and lineage.
public sealed class GeneratedQuestionStoreDetachmentTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-11-28T12:00:00Z");
    private const string Provenance = """{"source":"ai-realtime","model":"synthetic-model-marker","route":"r1"}""";
    private const string Lineage = """[{"kind":"story","id":"0b8e2f2a-0000-0000-0000-000000000001","version":3}]""";
    private static GeneratedQuestionWrite Write(Guid s, Guid o, IReadOnlyList<string> followUps) =>
        GeneratedQuestionStorePortTests.Write(s, o, followUps: followUps) with { ProvenanceJson = Provenance, LineageJson = Lineage };

    private static async Task<(InMemoryRuntimeStore Store, Guid Session, Guid Id)> Seeded()
    {
        var store = new InMemoryRuntimeStore(); var session = Guid.NewGuid(); var op = Guid.NewGuid();
        var id = await store.UpdateWithGeneratedQuestionsAsync(new(session, [op], []), (_, unit) => unit.Add(Write(session, op, ["Original follow-up"])));
        return (store, session, id);
    }

    [Fact]
    public async Task Changing_a_returned_snapshot_cannot_change_a_fresh_read()
    {
        var (store, _, _) = await Seeded();
        var snapshot = Assert.Single(store.GeneratedQuestionsSnapshot());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)snapshot.Question.FollowUps)[0] = "tampered");
        Assert.Equal("Original follow-up", Assert.Single(store.GeneratedQuestionsSnapshot()).Question.FollowUps[0]);
    }

    [Fact]
    public async Task A_failed_update_that_touched_a_loaded_row_commits_nothing_in_either_half()
    {
        var (store, session, id) = await Seeded(); var marker = Guid.NewGuid();
        var before = System.Text.Json.JsonSerializer.Serialize(store.GeneratedQuestionsSnapshot());
        await Assert.ThrowsAnyAsync<Exception>(() => store.UpdateWithGeneratedQuestionsAsync<bool>(new(session, [], [id]), (s, unit) =>
        {
            s.RetiredSessions.Add(marker);
            var loaded = unit.ById(id)!;
            try { ((IList<string>)loaded.Question.FollowUps)[0] = "tampered"; } catch (NotSupportedException) { }
            throw new InvalidOperationException("fence failed");
        }));
        Assert.DoesNotContain(marker, await store.ReadAsync(s => s.RetiredSessions));
        Assert.Equal(before, System.Text.Json.JsonSerializer.Serialize(store.GeneratedQuestionsSnapshot()));
    }

    [Fact]
    public async Task Changing_the_callers_list_after_Add_cannot_change_the_payload_or_bypass_the_limit()
    {
        var store = new InMemoryRuntimeStore(); var session = Guid.NewGuid(); var op = Guid.NewGuid();
        var followUps = new List<string> { "Short follow-up" };
        await store.UpdateWithGeneratedQuestionsAsync(new(session, [op], []), (_, unit) =>
        {
            var id = unit.Add(Write(session, op, followUps));
            followUps[0] = new string('x', GeneratedQuestionUnit.MaximumFollowUp + 1);   // after validation
            followUps.Add("extra");
            return id;
        });
        var stored = Assert.Single(store.GeneratedQuestionsSnapshot()).Question.FollowUps;
        Assert.Equal(["Short follow-up"], stored);
    }

    [Fact]
    public async Task Changing_the_callers_scope_after_the_unit_is_built_cannot_widen_what_it_allows()
    {
        var store = new InMemoryRuntimeStore(); var session = Guid.NewGuid(); var allowed = Guid.NewGuid(); var sneaked = Guid.NewGuid();
        var operations = new List<Guid> { allowed };
        var error = await Assert.ThrowsAsync<DomainException>(() => store.UpdateWithGeneratedQuestionsAsync(new(session, operations, []), (_, unit) =>
        {
            operations.Add(sneaked);
            return unit.Add(Write(session, sneaked, []));
        }));
        Assert.Equal(409, error.StatusCode); Assert.Empty(store.GeneratedQuestionsSnapshot());
    }

    [Fact]
    public async Task Provenance_and_lineage_round_trip_purge_clears_lineage_and_erasure_removes_both()
    {
        var (store, session, id) = await Seeded();
        var row = Assert.Single(store.GeneratedQuestionsSnapshot());
        Assert.Equal(Provenance, row.ProvenanceJson); Assert.Equal(Lineage, row.LineageJson);
        var loaded = await store.UpdateWithGeneratedQuestionsAsync(new(session, [], [id]), (_, unit) => unit.ById(id));
        Assert.Equal(Lineage, loaded!.LineageJson);
        await store.UpdateWithGeneratedQuestionsAsync(new(session, [], [id]), (_, unit) => { unit.Purge([id], At); return true; });
        var purged = Assert.Single(store.GeneratedQuestionsSnapshot());
        Assert.Equal("[]", purged.LineageJson); Assert.Equal("", purged.Fingerprint); Assert.Null(purged.Question.Prompt);
        Assert.Equal(Provenance, purged.ProvenanceJson);   // route/model provenance is content-free and kept with the tombstone
        store.EraseGeneratedQuestions();
        Assert.Empty(store.GeneratedQuestionsSnapshot());
    }
}

public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task Generated_store_port_commits_atomically_rolls_back_purges_to_a_tombstone_and_serializes_writers()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var host = new PostgresApiFactory(database.ConnectionString);
        var store = host.Services.GetRequiredService<IRuntimeStore>();
        var session = Guid.NewGuid(); var op = Guid.NewGuid(); var marker = Guid.NewGuid();

        await Assert.ThrowsAsync<InvalidOperationException>(() => store.UpdateWithGeneratedQuestionsAsync<bool>(new(session, [op], []), (s, unit) =>
        {
            s.RetiredSessions.Add(marker); unit.Add(GeneratedQuestionStorePortTests.Write(session, op)); throw new InvalidOperationException("fence failed");
        }));
        Assert.DoesNotContain(marker, await store.ReadAsync(s => s.RetiredSessions));

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ =>
        {
            try { return await store.UpdateWithGeneratedQuestionsAsync(new(session, [op], []), (_, unit) => unit.Add(GeneratedQuestionStorePortTests.Write(session, op))); }
            catch (DomainException e) when (e.StatusCode == 409) { return Guid.Empty; }
        }));
        var id = Assert.Single(results, r => r != Guid.Empty);
        Assert.Equal(GeneratedQuestionIds.For(session, op), id);

        var reader = host.Services.GetRequiredService<IGeneratedPracticeQuestionReader>();
        Assert.Equal("How would you shard a write-heavy table?", (await reader.GetAsync(session, id))!.Prompt);
        await store.UpdateWithGeneratedQuestionsAsync(new(session, [], [id]), (_, unit) => { unit.Purge([id], DateTimeOffset.UtcNow); return true; });
        var purged = await reader.GetAsync(session, id);
        Assert.Null(purged!.Prompt); Assert.NotNull(purged.PurgedAt);
        using (var scope = host.Services.CreateScope())
        {
            var db = await scope.ServiceProvider.GetRequiredService<IDbContextFactory<SessionDbContext>>().CreateDbContextAsync();
            var row = await db.GeneratedPracticeQuestions.AsNoTracking().SingleAsync(r => r.Id == id);
            Assert.Null(row.Json); Assert.Equal("", row.Fingerprint); Assert.Equal("[]", row.Lineage);
        }
        var meta = GeneratedQuestionStorePortTests.Write(session, Guid.NewGuid()) with { ProvenanceJson = """{"model":"synthetic-model-marker"}""", LineageJson = """[{"kind":"story","version":3}]""" };
        var metaId = await store.UpdateWithGeneratedQuestionsAsync(new(session, [meta.OperationId], []), (_, unit) => unit.Add(meta));
        var record = await store.UpdateWithGeneratedQuestionsAsync(new(session, [], [metaId]), (_, unit) => unit.ById(metaId));
        // PostgreSQL keeps the same supplied provenance and lineage as the in-memory adapter (review SG2 parity).
        Assert.Contains("synthetic-model-marker", record!.ProvenanceJson); Assert.Contains("\"version\":3", record.LineageJson.Replace(" ", ""));
    }

    [PostgresFact]
    public async Task Whole_Core_erasure_deletes_generated_rows_but_keeps_the_table()
    {
        await using var database = await TestDatabase.CreateAsync();
        using var host = new PostgresApiFactory(database.ConnectionString);
        var store = host.Services.GetRequiredService<IRuntimeStore>(); var session = Guid.NewGuid(); var op = Guid.NewGuid();
        await store.UpdateWithGeneratedQuestionsAsync(new(session, [op], []), (_, unit) => unit.Add(GeneratedQuestionStorePortTests.Write(session, op)));
        var privacy = host.Services.GetRequiredService<IPrivacyStore>(); var status = await privacy.StatusAsync();
        await privacy.EraseAsync(new(Guid.NewGuid(), status.Revision, PrivacyData.ErasePhrase), new string('a', 64), DateTimeOffset.UtcNow);
        var db = await host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>().CreateDbContextAsync();
        Assert.Equal(0, await db.GeneratedPracticeQuestions.CountAsync());
        Assert.Contains("20261128034712_GeneratedPracticeQuestions", await db.Database.GetAppliedMigrationsAsync());
    }
}
