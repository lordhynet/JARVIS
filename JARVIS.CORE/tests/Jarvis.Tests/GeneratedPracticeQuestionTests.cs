using System.Text.Json;
using Jarvis.Api.Voice;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Training;
using Jarvis.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Jarvis.Tests;

// Slice 2a: storage and read-only resolver scaffolding. Synthetic fixtures only; nothing in the app writes generated rows.
public sealed class GeneratedPracticeQuestionTests
{
    private sealed class FakeReader(params GeneratedPracticeQuestion[] rows) : IGeneratedPracticeQuestionReader
    {
        public Task<GeneratedPracticeQuestion?> GetAsync(Guid sessionId, Guid id, CancellationToken ct = default) =>
            Task.FromResult(rows.FirstOrDefault(r => r.Id == id));   // deliberately ignores session: the resolver must enforce it
        public Task<IReadOnlyList<GeneratedPracticeQuestion>> ListAsync(Guid sessionId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<GeneratedPracticeQuestion>>(rows);
    }

    private static readonly Guid SessionA = Guid.NewGuid(), SessionB = Guid.NewGuid();
    private static GeneratedPracticeQuestion Row(Guid session, LearningDomain topic = LearningDomain.SystemDesign, DateTimeOffset? purged = null,
        int minutes = 0) => new(Guid.NewGuid(), session, topic, "consistency", 1, 1, purged is null ? "How would you keep two regions consistent?" : null,
        purged is null ? "Discuss quorum, conflict resolution and the trade-off with latency." : null, ["What if one region is down?"],
        DateTimeOffset.UnixEpoch.AddMinutes(minutes), purged);

    [Fact]
    public async Task Authored_questions_resolve_exactly_as_the_bank_with_authored_verified_origin()
    {
        var resolver = new PracticeQuestionResolver(new FakeReader());
        foreach (var question in QuestionBank.All)
        {
            var lookup = await resolver.ResolveAsync(SessionA, question.Id);
            Assert.Same(question, lookup.Question!.Question);
            Assert.Equal(PracticeQuestionOrigin.Authored, lookup.Question.Origin); Assert.True(lookup.Question.Verified);
        }
        Assert.Equal(QuestionBank.All, (await resolver.ListAsync(Guid.Empty)).Select(q => q.Question));
    }

    [Fact]
    public async Task Generated_questions_resolve_only_in_their_own_session_and_are_never_verified()
    {
        var row = Row(SessionA);
        var resolver = new PracticeQuestionResolver(new FakeReader(row));
        var own = await resolver.ResolveAsync(SessionA, row.Id);
        Assert.Equal(PracticeQuestionOrigin.Generated, own.Question!.Origin); Assert.False(own.Question.Verified);
        Assert.Equal(row.Prompt, own.Question.Question.Prompt); Assert.Equal("", own.Question.Question.SourceUrl);
        Assert.Same(PracticeQuestionLookup.Unknown, await resolver.ResolveAsync(SessionB, row.Id));
        Assert.Same(PracticeQuestionLookup.Unknown, await resolver.ResolveAsync(Guid.Empty, row.Id));
        Assert.DoesNotContain((await resolver.ListAsync(SessionB)), q => q.Question.Id == row.Id);
    }

    [Fact]
    public async Task A_purged_question_is_removed_not_unknown_authored_or_verified()
    {
        var row = Row(SessionA, purged: DateTimeOffset.UnixEpoch);
        var resolver = new PracticeQuestionResolver(new FakeReader(row));
        var lookup = await resolver.ResolveAsync(SessionA, row.Id);
        Assert.True(lookup.Purged); Assert.Null(lookup.Question);
        Assert.DoesNotContain(await resolver.ListAsync(SessionA), q => q.Question.Id == row.Id);
    }

    [Fact]
    public async Task Listing_keeps_authored_first_then_the_sessions_generated_items_filtered_by_topic()
    {
        var later = Row(SessionA, minutes: 5); var earlier = Row(SessionA, minutes: 1); var other = Row(SessionA, LearningDomain.Databases);
        var resolver = new PracticeQuestionResolver(new FakeReader(later, earlier, other, Row(SessionB)));
        var list = await resolver.ListAsync(SessionA, LearningDomain.SystemDesign);
        var authored = QuestionBank.All.Count(q => q.Domain == LearningDomain.SystemDesign);
        Assert.All(list.Take(authored), q => Assert.Equal(PracticeQuestionOrigin.Authored, q.Origin));
        Assert.Equal([earlier.Id, later.Id], list.Skip(authored).Select(q => q.Question.Id));
    }

    [Fact]
    public async Task An_authored_ID_always_wins_over_a_generated_row()
    {
        var authored = QuestionBank.All[0];
        var clash = Row(SessionA) with { Id = authored.Id };
        var lookup = await new PracticeQuestionResolver(new FakeReader(clash)).ResolveAsync(SessionA, authored.Id);
        Assert.Equal(PracticeQuestionOrigin.Authored, lookup.Question!.Origin);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("""{"Prompt":"","ReferenceOutline":"x","FollowUps":[]}""")]
    [InlineData("""{"Prompt":"q","ReferenceOutline":null,"FollowUps":[]}""")]
    [InlineData("""{"Prompt":"q","ReferenceOutline":"x","FollowUps":[null]}""")]
    public void Malformed_rows_are_refused_with_a_fixed_message_that_never_echoes_stored_text(string json)
    {
        var row = new GeneratedPracticeQuestionRow { Id = Guid.NewGuid(), SessionId = SessionA, Topic = (int)LearningDomain.SystemDesign,
            OutlineKey = "consistency", Version = 1, Json = json };
        var error = Assert.Throws<InvalidDataException>(() => row.Contract());
        Assert.Equal("Invalid generated practice question row.", error.Message);
    }

    [Fact]
    public void Undefined_topic_or_missing_identity_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => new GeneratedPracticeQuestionRow { Id = Guid.NewGuid(), SessionId = SessionA, Topic = 99, OutlineKey = "x", Version = 1, Json = "{}" }.Contract());
        Assert.Throws<InvalidDataException>(() => new GeneratedPracticeQuestionRow { Id = Guid.NewGuid(), SessionId = Guid.Empty, Topic = 1, OutlineKey = "x", Version = 1, Json = "{}" }.Contract());
    }

    [Fact]
    public void In_memory_hosts_read_empty_and_no_voice_tool_can_create_generated_questions()
    {
        using var host = new ProviderFactory(_ => throw new InvalidOperationException("No provider call"));
        Assert.IsType<EmptyGeneratedPracticeQuestionReader>(host.Services.GetRequiredService<IGeneratedPracticeQuestionReader>());
        Assert.NotNull(host.Services.GetRequiredService<PracticeQuestionResolver>());
        var names = CoreVoiceTools.Definitions.Select(d => JsonSerializer.SerializeToElement(d).GetProperty("name").GetString()!);
        Assert.DoesNotContain(names, n => n.Contains("generat", StringComparison.OrdinalIgnoreCase));
    }
}

public sealed partial class PostgresSessionTests
{
    private const string BeforeGeneratedQuestions = "20261127024702_CanonicalTrainingRows";

    [PostgresFact]
    public async Task Generated_question_table_migrates_reads_by_session_and_refuses_a_populated_downgrade_without_mutation()
    {
        await using var database = await TestDatabase.CreateAsync(); await using var db = SchemaContext(database.ConnectionString);
        await db.Database.MigrateAsync();
        Assert.Contains("20261128034712_GeneratedPracticeQuestions", await db.Database.GetAppliedMigrationsAsync());
        var session = Guid.NewGuid(); var id = Guid.NewGuid();
        db.GeneratedPracticeQuestions.Add(new() { Id = id, SessionId = session, OperationId = Guid.NewGuid(), Topic = (int)LearningDomain.SystemDesign,
            OutlineKey = "consistency", OutlineVersion = 1, Version = 1, Fingerprint = new string('a', 64),
            Json = """{"Prompt":"Synthetic prompt","ReferenceOutline":"Synthetic outline","FollowUps":[]}""", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(); db.ChangeTracker.Clear();

        var reader = new PostgresGeneratedPracticeQuestionReader(new TestContextFactory(database.ConnectionString));
        Assert.Equal("Synthetic prompt", (await reader.GetAsync(session, id))!.Prompt);
        Assert.Null(await reader.GetAsync(Guid.NewGuid(), id));
        Assert.Empty(await reader.ListAsync(Guid.NewGuid()));

        await Assert.ThrowsAsync<PostgresException>(() => db.Database.GetService<IMigrator>().MigrateAsync(BeforeGeneratedQuestions));
        Assert.Contains("20261128034712_GeneratedPracticeQuestions", await db.Database.GetAppliedMigrationsAsync());
        Assert.Equal(1, await db.GeneratedPracticeQuestions.CountAsync());
    }

    [PostgresFact]
    public async Task Generated_question_table_downgrades_cleanly_while_empty()
    {
        await using var database = await TestDatabase.CreateAsync(); await using var db = SchemaContext(database.ConnectionString);
        await db.Database.MigrateAsync();
        await db.Database.GetService<IMigrator>().MigrateAsync(BeforeGeneratedQuestions);
        Assert.DoesNotContain("20261128034712_GeneratedPracticeQuestions", await db.Database.GetAppliedMigrationsAsync());
        Assert.False(await db.Database.SqlQueryRaw<bool>("SELECT to_regclass('\"GeneratedPracticeQuestions\"') IS NOT NULL AS \"Value\"").SingleAsync());
        await db.Database.MigrateAsync();
        Assert.Contains("20261128034712_GeneratedPracticeQuestions", await db.Database.GetAppliedMigrationsAsync());
    }

    private sealed class TestContextFactory(string connection) : IDbContextFactory<SessionDbContext>
    {
        public SessionDbContext CreateDbContext() => SchemaContext(connection);
    }
}

public sealed class PracticeQuestionListPrecedenceTests
{
    private static readonly Guid Session = Guid.NewGuid();
    private sealed class Reader(params GeneratedPracticeQuestion[] rows) : IGeneratedPracticeQuestionReader
    {
        public Task<GeneratedPracticeQuestion?> GetAsync(Guid sessionId, Guid id, CancellationToken ct = default) => Task.FromResult(rows.FirstOrDefault(r => r.Id == id));
        public Task<IReadOnlyList<GeneratedPracticeQuestion>> ListAsync(Guid sessionId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<GeneratedPracticeQuestion>>(rows);
    }
    private static GeneratedPracticeQuestion Row(Guid id, LearningDomain topic, int minutes, string prompt = "Synthetic generated prompt") =>
        new(id, Session, topic, "outline", 1, 1, prompt, "Synthetic outline", [], DateTimeOffset.UnixEpoch.AddMinutes(minutes), null);

    [Theory]
    [InlineData(null)]
    [InlineData(LearningDomain.SystemDesign)]
    [InlineData(LearningDomain.Databases)]   // a different topic from the authored question it collides with
    public async Task List_applies_the_same_authored_precedence_as_resolve(LearningDomain? topic)
    {
        var authored = QuestionBank.All.First(q => q.Domain == LearningDomain.SystemDesign);
        var resolver = new PracticeQuestionResolver(new Reader(Row(authored.Id, topic ?? LearningDomain.SystemDesign, 1)));
        var list = await resolver.ListAsync(Session, topic);
        Assert.DoesNotContain(list, q => q.Question.Id == authored.Id && q.Origin == PracticeQuestionOrigin.Generated);
        Assert.True(list.Count(q => q.Question.Id == authored.Id) <= 1);
        Assert.Equal(list.Count, list.Select(q => q.Question.Id).Distinct().Count());
        Assert.Equal(PracticeQuestionOrigin.Authored, (await resolver.ResolveAsync(Session, authored.Id)).Question!.Origin);
    }

    [Fact]
    public async Task Duplicate_generated_IDs_list_once_keeping_the_earliest_row()
    {
        var id = Guid.NewGuid();
        var resolver = new PracticeQuestionResolver(new Reader(Row(id, LearningDomain.Databases, 5, "later"), Row(id, LearningDomain.Databases, 1, "earlier")));
        var generated = Assert.Single(await resolver.ListAsync(Session), q => q.Origin == PracticeQuestionOrigin.Generated);
        Assert.Equal("earlier", generated.Question.Prompt);
    }
}
