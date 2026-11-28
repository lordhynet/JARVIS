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
