using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Recordings;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Jarvis.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;
public sealed class PostgresTheoryAttribute : TheoryAttribute
{
    public PostgresTheoryAttribute() {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("JARVIS_TEST_POSTGRES")))
            Skip = "Set JARVIS_TEST_POSTGRES to a PostgreSQL connection with CREATE DATABASE permission.";
    }
}
public sealed partial class PostgresSessionTests
{
    [PostgresTheory]
    [InlineData(true)] [InlineData(false)]
    public async Task Recorded_practice_restarts_at_the_next_question_and_source_delete_redacts_every_session_copy(bool deleteRecording)
    {
        await using var database = await TestDatabase.CreateAsync(); var files = new RecordingFilesFake(); var clock = new TravelClock();
        Guid recording, document, sessionId; using var manual = CoreInvocationContext.Enter(InvocationOrigin.ManualClient);
        using (var root = new PostgresApiFactory(database.ConnectionString, clock))
        using (var host = root.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IRecordingAudioFiles>(files)))) {
            var services = host.Services; var source = await RecordedInterviewFixture.Seed(services, files); recording = source.Recording; document = source.Document;
            var practice = await services.GetRequiredService<RecordedInterviewPractice>().StartAsync(recording, new(Guid.NewGuid(), 1, "practice", true, false)); sessionId = practice.SessionId;
            await VoiceToolFixture.OpenAsync(services, sessionId); var answer = await RecordedInterviewFixture.Say(services, sessionId, "I chose retries because the constraint was temporary failure.");
            await RecordedInterviewFixture.Run(services, sessionId, "answer", answer);
            await services.GetRequiredService<SessionService>().CheckpointAsync(sessionId, new("Private source question", "Sensitive checkpoint"));
            var saved = await services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.VoiceCoachings[practice.Id]);
            Assert.DoesNotContain("Why did", JsonSerializer.Serialize(saved)); Assert.DoesNotContain("I chose retries", JsonSerializer.Serialize(saved));
        }
        using var restartedRoot = new PostgresApiFactory(database.ConnectionString, clock);
        using var restarted = restartedRoot.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IRecordingAudioFiles>(files))); var nextServices = restarted.Services;
        await VoiceToolFixture.OpenAsync(nextServices, sessionId); var current = (await RecordedInterviewFixture.Run(nextServices, sessionId, "current"))!;
        Assert.Equal(2, current.QuestionNumber); Assert.Equal("Which evidence supports the result?", current.Prompt!.Question); Assert.NotNull(current.Recorded);
        if (deleteRecording) await nextServices.GetRequiredService<RecordingService>().DeleteAsync(recording, 1);
        else await nextServices.GetRequiredService<ImportService>().DeleteAsync(document, 1);
        var contexts = nextServices.GetRequiredService<IDbContextFactory<SessionDbContext>>(); await using var db = await contexts.CreateDbContextAsync();
        var row = await db.Sessions.SingleAsync(s => s.Id == sessionId); Assert.Equal("[]", row.MessagesJson); Assert.Null(row.VoiceSummaryJson); Assert.Null(row.CurrentTask); Assert.Equal(SessionStatus.Ended, row.Status);
        Assert.Empty(await db.Checkpoints.Where(c => c.SessionId == sessionId).ToArrayAsync()); Assert.Empty(await db.RuntimeVoiceTurns.ToArrayAsync());
        Assert.Empty(await nextServices.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.VoiceCoachings.Values.ToArray()));
        await Assert.ThrowsAsync<DomainException>(() => RecordedInterviewFixture.Say(nextServices, sessionId, "Late private reply"));
    }
    [PostgresFact]
    public async Task Failed_recording_file_deletion_rolls_back_private_session_redaction()
    {
        await using var database = await TestDatabase.CreateAsync(); var files = new RecordingFilesFake(); using var root = new PostgresApiFactory(database.ConnectionString);
        using var host = root.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IRecordingAudioFiles>(files))); var services = host.Services;
        using var manual = CoreInvocationContext.Enter(InvocationOrigin.ManualClient); var source = await RecordedInterviewFixture.Seed(services, files);
        var practice = await services.GetRequiredService<RecordedInterviewPractice>().StartAsync(source.Recording, new(Guid.NewGuid(), 1, "practice", true, false));
        await VoiceToolFixture.OpenAsync(services, practice.SessionId); await RecordedInterviewFixture.Say(services, practice.SessionId, "Keep this answer until removal succeeds.");
        files.FailRecordingDelete = true; await Assert.ThrowsAsync<IOException>(() => services.GetRequiredService<RecordingService>().DeleteAsync(source.Recording, 1));
        Assert.NotEmpty((await services.GetRequiredService<ISessionReader>().GetAsync(practice.SessionId))!.Messages!);
        Assert.Single((await services.GetRequiredService<RecordedInterviewPractice>().GetAsync(source.Recording, false)).Practices);
        files.FailRecordingDelete = false; await services.GetRequiredService<RecordingService>().DeleteAsync(source.Recording, 1);
        Assert.Empty((await services.GetRequiredService<ISessionReader>().GetAsync(practice.SessionId))!.Messages!);
    }
    [PostgresFact]
    public async Task Interview_transcript_retention_removes_private_answers_and_checkpoint_copies_in_the_same_transaction()
    {
        await using var database = await TestDatabase.CreateAsync(); var files = new RecordingFilesFake(); var clock = new TravelClock(); using var root = new PostgresApiFactory(database.ConnectionString, clock);
        using var host = root.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IRecordingAudioFiles>(files))); var services = host.Services;
        using var manual = CoreInvocationContext.Enter(InvocationOrigin.ManualClient); var source = await RecordedInterviewFixture.Seed(services, files);
        var practice = await services.GetRequiredService<RecordedInterviewPractice>().StartAsync(source.Recording, new(Guid.NewGuid(), 1, "practice", true, false));
        await VoiceToolFixture.OpenAsync(services, practice.SessionId); await RecordedInterviewFixture.Say(services, practice.SessionId, "Private recap remains until source retention.");
        await services.GetRequiredService<SessionService>().CheckpointAsync(practice.SessionId, new("Private question", null));
        await services.GetRequiredService<IRuntimeStore>().UpdateAsync(s => { s.ImportedDocuments[source.Document] = s.ImportedDocuments[source.Document] with { ImportedAt = clock.Now.AddDays(-400) }; return true; });
        var privacy = services.GetRequiredService<IPrivacyStore>(); var policy = new RetentionPolicy(); var preview = await privacy.PreviewAsync(policy, clock.Now);
        await privacy.RetainAsync(new(Guid.NewGuid(), clock.Now, preview.PolicyFingerprint, PrivacyData.RetentionPhrase), policy, clock.Now);
        var session = (await services.GetRequiredService<ISessionReader>().GetAsync(practice.SessionId))!;
        Assert.Equal(SessionStatus.Ended, session.Status); Assert.Empty(session.Messages!); Assert.Empty(session.Checkpoints); Assert.Null(session.VoiceSummary);
        Assert.Empty(await services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.VoiceCoachings.Values.ToArray()));
    }
}
