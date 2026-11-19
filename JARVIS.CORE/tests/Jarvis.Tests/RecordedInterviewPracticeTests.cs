using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Coaching;
using Jarvis.Orchestrator.Intelligence;
using Jarvis.Orchestrator.Recordings;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Orchestrator.Sessions;
using Jarvis.Orchestrator.Workflows;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

internal static class RecordedInterviewFixture
{
    public static async Task<(Guid Recording, Guid Document, Guid[] UserPieces, Guid[] OtherPieces)> Seed(IServiceProvider services, IRecordingAudioFiles files)
    {
        var id = Guid.NewGuid(); var docId = Guid.NewGuid(); var user = new List<Guid>(); var other = new List<Guid>();
        var now = services.GetRequiredService<TimeProvider>().GetUtcNow(); var chunks = new List<RecordingChunk>(); var segments = new List<TranscriptSegment>();
        for (var sequence = 0; sequence < 2; sequence++) foreach (var track in new[] { "interviewer", "user" }) {
            var wave = RecordingRig.Wav(sequence == 0 ? 960000 : 160000); var piece = Guid.NewGuid();
            var chunk = new RecordingChunk(piece, id, track, sequence, RecordingAudio.Hash(wave), (wave.Length - 44) / 2, Guid.NewGuid(), 1, wave.Length, now);
            await files.WriteAsync(chunk, wave, default); chunks.Add(chunk); (track == "user" ? user : other).Add(piece);
            var text = track == "interviewer" ? sequence == 0 ? "Why did you choose retries?" : "Which evidence supports the result?" : sequence == 0 ? "I chose retries because the network failed." : "I do not know.";
            segments.Add(new(track, text, sequence * 60, sequence * 60 + (sequence == 0 ? 60 : 10), piece, track));
        }
        await services.GetRequiredService<IRecordingStore>().UpdateAsync((state, runtime) => {
            var view = new InterviewRecording(id, "seed-laptop", "Private mock interview", "Complete", ["user", "interviewer"], now, now, now, now,
                now, now.AddDays(30), DurationSamples: 70 * 16000, AudioBytes: chunks.Sum(c => c.Bytes), TranscriptId: docId,
                ConfirmStorageConsent: true, ConfirmRecordingAuthorised: true);
            state.Sessions.Add(id, new(view, "fixture-hash", "fixture-request")); foreach (var chunk in chunks) state.Chunks.Add(chunk.Id, chunk);
            runtime.ImportedDocuments.Add(docId, new(docId, "interview", view.Title, string.Join("\n", segments.Select(s => s.Text)), "fixture-hash", 1, now, segments,
                SourceRecordingId: id, TimingSource: "Capture intervals, not word timestamps")); return Task.FromResult(true);
        });
        return (id, docId, user.ToArray(), other.ToArray());
    }
    public static Task<VoiceCoachingView?> Run(IServiceProvider services, Guid session, string action, Guid utterance = default) =>
        services.GetRequiredService<VoiceCoachingService>().RunAsync(session, action, utterance, null, null, null, null, default);
    public static async Task<Guid> Say(IServiceProvider services, Guid session, string text, bool restricted = false) {
        var id = Guid.NewGuid(); await services.GetRequiredService<VoiceTranscriptService>().RecordAsync(session, id, "user", text, 10, default, restricted, new(10, 2, SpeechDelivery.PcmQuietMethod)); return id;
    }
}
public sealed class RecordedInterviewPracticeTests
{
    [Fact]
    public void Empty_story_fields_are_not_evidence_matches_for_question_words()
    {
        var now = DateTimeOffset.UtcNow; var id = Guid.NewGuid(); var state = new RuntimeState();
        state.Stories[id] = new(id, new("Unrelated draft", "", ""), 1, now, now);
        var document = new ImportedDocument(Guid.NewGuid(), "interview", "Fixture", "Which problem and constraints did you face?", "fixture", 1, now,
            [new("interviewer", "Which problem and constraints did you face?", 0, 60)]);
        Assert.Empty(Assert.Single(RecordedInterviewPractice.Questions(state, document)).Stories);
    }
    [Fact]
    public async Task Timeline_preserves_capture_provenance_estimates_and_owner_approved_cards_only()
    {
        var files = new RecordingFilesFake(); using var root = new JarvisApiFactory(); using var host = root.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IRecordingAudioFiles>(files)));
        using var manual = CoreInvocationContext.Enter(InvocationOrigin.ManualClient); var services = host.Services; var source = await RecordedInterviewFixture.Seed(services, files);
        var service = services.GetRequiredService<RecordedInterviewPractice>();
        var cv = await services.GetRequiredService<ImportService>().ImportAsync(new(Guid.NewGuid(), "cv", "My supplied evidence", "I chose retries because the network failed.", true));
        var storyId = Guid.NewGuid(); await services.GetRequiredService<IRuntimeStore>().UpdateAsync(s => {
            var now = DateTimeOffset.UtcNow; s.Stories.Add(storyId, new(storyId, new("Retries after network failure", "Project", "Retries"), 1, now, now));
            var quote = "I chose retries"; var claimId = Guid.NewGuid(); s.SourceClaims.Add(claimId, new(claimId, cv.Id, cv.Revision, storyId, quote, 0, quote.Length, now)); return true;
        });
        var page = await service.GetAsync(source.Recording, false);
        Assert.Equal(2, page.Questions.Count); Assert.Equal(source.UserPieces[0], Assert.Single(page.Questions[0].AudioPieces)); Assert.Equal(7, page.Questions[0].Delivery.Words);
        Assert.Equal(7, page.Questions[0].Delivery.WordsPerMinute); Assert.Null(page.Questions[0].Delivery.PauseRatio); Assert.Contains("Capture-interval", page.Questions[0].Alignment);
        Assert.Equal("I chose retries", Assert.Single(Assert.Single(page.Questions[0].Stories).Claims).Quote);
        Assert.Empty(await services.GetRequiredService<IRuntimeStore>().ReadAsync(s => s.Learning.Values.ToArray()));
        await Assert.ThrowsAsync<DomainException>(() => service.ApproveCardAsync(source.Recording, new(1, page.Questions[0].Id, false, false)));
        var card = await service.ApproveCardAsync(source.Recording, new(1, page.Questions[0].Id, true, false)); Assert.Equal(card, await service.ApproveCardAsync(source.Recording, new(1, page.Questions[0].Id, true, false)));
        Assert.DoesNotContain("Why", JsonSerializer.Serialize(card)); Assert.False(await services.GetRequiredService<IRuntimeStore>().ReadAsync(s => AgentRuntime.SourceCurrent(s, new("learning", card.Id, AgentRuntime.LearningVersion(card)))));
        Assert.True((await service.GetAsync(source.Recording, false)).Questions[0].CardApproved);
        Assert.Equal(files.Blobs.Values.First(), await service.ClipAsync(source.Recording, page.Questions[0].Id, source.UserPieces[0], 1, false));
        await Assert.ThrowsAsync<DomainException>(() => service.ClipAsync(source.Recording, page.Questions[0].Id, source.OtherPieces[0], 1, false));
        await services.GetRequiredService<IRuntimeStore>().UpdateAsync(s => {
            var doc = s.ImportedDocuments[source.Document]; s.ImportedDocuments[doc.Id] = doc with { Revision = 2, Segments = doc.Segments.Select(seg => seg.CaptureTrack == "interviewer" ? seg with { Speaker = "user" } : seg).ToArray() }; return true;
        });
        Assert.Empty((await service.GetAsync(source.Recording, false)).Questions); // Mislabelled loopback never becomes a playable Me answer.
    }
    [Fact]
    public async Task Practice_uses_fresh_canonical_speech_compares_each_answer_and_never_becomes_agent_or_memory_context()
    {
        var files = new RecordingFilesFake(); var provider = new RecordingTranscriberFake(); using var root = new JarvisApiFactory();
        using var host = root.WithWebHostBuilder(b => b.ConfigureServices(s => { s.AddSingleton<IRecordingAudioFiles>(files); s.AddSingleton<IRecordingTranscriber>(provider); }));
        using var manual = CoreInvocationContext.Enter(InvocationOrigin.ManualClient); var services = host.Services; var source = await RecordedInterviewFixture.Seed(services, files);
        var service = services.GetRequiredService<RecordedInterviewPractice>(); var request = new StartRecordedPractice(Guid.NewGuid(), 1, "practice", true, false);
        await Assert.ThrowsAsync<DomainException>(() => service.StartAsync(source.Recording, request with { ConfirmCloudPractice = false }));
        var practice = await service.StartAsync(source.Recording, request); Assert.Equal(practice, await service.StartAsync(source.Recording, request));
        await VoiceToolFixture.OpenAsync(services, practice.SessionId);
        var denied = await services.GetRequiredService<Jarvis.Api.Voice.CoreVoiceTools>().ExecuteAsync("core_context", "{\"section\":\"overview\"}", practice.SessionId, new HashSet<Guid>(), default);
        Assert.Contains("only supports its selected coaching", denied);
        Assert.Equal("Why did you choose retries?", (await RecordedInterviewFixture.Run(services, practice.SessionId, "current"))!.Prompt!.Question);
        var locked = await RecordedInterviewFixture.Say(services, practice.SessionId, "Restricted answer", true);
        await Assert.ThrowsAsync<DomainException>(() => RecordedInterviewFixture.Run(services, practice.SessionId, "answer", locked));
        var answer = await RecordedInterviewFixture.Say(services, practice.SessionId, "I chose retries because evidence measured the failure. The next point is recovery.");
        var next = (await RecordedInterviewFixture.Run(services, practice.SessionId, "answer", answer))!;
        Assert.Equal(2, next.QuestionNumber); Assert.Equal(7, next.Recorded!.Before.WordsPerMinute); Assert.Equal(.2, next.Recorded.After.PauseRatio); Assert.Equal(10, next.Recorded.After.Seconds);
        Assert.Equal("Which evidence supports the result?", next.Prompt!.Question);
        await RecordedInterviewFixture.Run(services, practice.SessionId, "answer", answer); // Lost-response replay does not advance twice.
        var runtime = services.GetRequiredService<IRuntimeStore>(); var turn = await runtime.ReadAsync(s => s.VoiceTurns[answer]);
        Assert.False(await runtime.ReadAsync(s => AgentRuntime.SourceCurrent(s, new("voice", answer, PrivacyData.Hash(JsonSerializer.Serialize(turn))))));
        await Assert.ThrowsAsync<DomainException>(() => services.GetRequiredService<ContextMemoryService>().SaveAsync("general", new(Guid.NewGuid(), "voice", answer, null, DateTimeOffset.UtcNow.AddDays(2), Length: 10)));
        var final = await RecordedInterviewFixture.Say(services, practice.SessionId, "The evidence is measured; the source is unknown.");
        Assert.Equal("Completed", (await RecordedInterviewFixture.Run(services, practice.SessionId, "answer", final))!.Status);
        var history = Assert.Single((await service.GetAsync(source.Recording, false)).Practices); Assert.Equal(2, history.Answers!.Count); Assert.Equal(turn.Text, history.Answers[0].CapturedAnswer);
        Assert.Equal(0, provider.Calls); Assert.Empty(await runtime.ReadAsync(s => s.UsageReservations.Values.ToArray()));
    }
    [Fact]
    public async Task Private_practice_refuses_locked_reads_and_changed_sources_but_recap_needs_no_transcript()
    {
        var files = new RecordingFilesFake(); using var root = new JarvisApiFactory(); using var host = root.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IRecordingAudioFiles>(files)));
        using var manual = CoreInvocationContext.Enter(InvocationOrigin.ManualClient); var services = host.Services; var source = await RecordedInterviewFixture.Seed(services, files);
        var service = services.GetRequiredService<RecordedInterviewPractice>(); var practice = await service.StartAsync(source.Recording, new(Guid.NewGuid(), 1, "practice", true, false));
        await VoiceToolFixture.OpenAsync(services, practice.SessionId); var runtime = services.GetRequiredService<IRuntimeStore>();
        await runtime.UpdateAsync(s => { s.AudioLease = s.AudioLease! with { Locked = true }; return true; });
        await Assert.ThrowsAsync<DomainException>(() => RecordedInterviewFixture.Run(services, practice.SessionId, "current"));
        await runtime.UpdateAsync(s => { s.AudioLease = s.AudioLease! with { Locked = false }; s.ImportedDocuments[source.Document] = s.ImportedDocuments[source.Document] with { Revision = 2 }; return true; });
        await Assert.ThrowsAsync<DomainException>(() => RecordedInterviewFixture.Run(services, practice.SessionId, "current"));
        Assert.False(Assert.Single((await service.GetAsync(source.Recording, false)).Practices).SourceCurrent);
        await services.GetRequiredService<IRecordingStore>().UpdateAsync((state, s) => { s.ImportedDocuments.Remove(source.Document); var recording = state.Sessions[source.Recording]; state.Sessions[source.Recording] = recording with { View = recording.View with { TranscriptId = null } }; return Task.FromResult(true); });
        var recap = await service.StartAsync(source.Recording, new(Guid.NewGuid(), null, "recap", true, false)); await VoiceToolFixture.OpenAsync(services, recap.SessionId);
        var answer = await RecordedInterviewFixture.Say(services, recap.SessionId, "I felt prepared. Next step is to confirm the follow-up date."); await RecordedInterviewFixture.Run(services, recap.SessionId, "answer", answer);
        var page = await service.GetAsync(source.Recording, false); Assert.Empty(page.Questions); Assert.Equal("I felt prepared. Next step is to confirm the follow-up date.", page.Practices.Single(p => p.Mode == "recap").CapturedRecap);
    }
    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Deleting_either_source_removes_recap_cards_answers_checkpoints_and_all_session_copies(bool deleteRecording)
    {
        var files = new RecordingFilesFake(); using var root = new JarvisApiFactory(); using var host = root.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IRecordingAudioFiles>(files)));
        using var manual = CoreInvocationContext.Enter(InvocationOrigin.ManualClient); var services = host.Services; var source = await RecordedInterviewFixture.Seed(services, files);
        var service = services.GetRequiredService<RecordedInterviewPractice>(); var practice = await service.StartAsync(source.Recording, new(Guid.NewGuid(), 1, "practice", true, false));
        var recap = await service.StartAsync(source.Recording, new(Guid.NewGuid(), null, "recap", true, false));
        foreach (var sessionId in new[] { practice.SessionId, recap.SessionId }) {
            await VoiceToolFixture.OpenAsync(services, sessionId); var answer = await RecordedInterviewFixture.Say(services, sessionId, "Sensitive interview answer and next steps.");
            await RecordedInterviewFixture.Run(services, sessionId, "answer", answer);
            await services.GetRequiredService<SessionService>().CheckpointAsync(sessionId, new("Sensitive interviewer question", "private details"));
        }
        var page = await service.GetAsync(source.Recording, false); Assert.Equal("Sensitive interview answer and next steps.", page.Practices.Single(p => p.Mode == "recap").CapturedRecap);
        var card = await service.ApproveCardAsync(source.Recording, new(1, page.Questions[0].Id, true, false));
        await services.GetRequiredService<LearningService>().ReviewAsync(card.Id, new(Guid.NewGuid(), ReviewRating.Good, 5), default);
        await services.GetRequiredService<IRuntimeStore>().UpdateAsync(s => s.Learning.Remove(card.Id)); // A review snapshot must also cascade without its live card.
        if (deleteRecording) await services.GetRequiredService<RecordingService>().DeleteAsync(source.Recording, 1);
        else await services.GetRequiredService<ImportService>().DeleteAsync(source.Document, 1);
        foreach (var sessionId in new[] { practice.SessionId, recap.SessionId }) {
            var session = (await services.GetRequiredService<ISessionReader>().GetAsync(sessionId))!;
            Assert.Equal(SessionStatus.Ended, session.Status); Assert.Empty(session.Messages!); Assert.Empty(session.Checkpoints); Assert.Null(session.VoiceSummary); Assert.Null(session.CurrentTask);
            await Assert.ThrowsAsync<DomainException>(() => RecordedInterviewFixture.Say(services, sessionId, "Late response must not resurrect private data."));
        }
        await services.GetRequiredService<IRuntimeStore>().ReadAsync(s => { Assert.Empty(s.VoiceCoachings); Assert.Empty(s.VoiceTurns); Assert.Empty(s.Learning); Assert.Empty(s.Reviews); Assert.Null(s.AudioLease); return true; });
        await Assert.ThrowsAsync<DomainException>(() => service.GetAsync(source.Recording, false));
    }
    [Fact]
    public async Task Routes_require_owner_unlocked_access_and_elevated_inline_audio_with_no_cache()
    {
        var files = new RecordingFilesFake(); using var root = new JarvisApiFactory(); using var host = root.WithWebHostBuilder(b => { TestOwnerManagement.Configure(b); b.ConfigureServices(s => s.AddSingleton<IRecordingAudioFiles>(files)); });
        var source = await RecordedInterviewFixture.Seed(host.Services, files); var path = $"/api/recordings/{source.Recording}/practice";
        using var client = host.CreateClient(); Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path + "?locked=false")).StatusCode);
        client.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path + "?locked=true")).StatusCode); Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(path)).StatusCode);
        var response = await client.GetAsync(path + "?locked=false"); Assert.True(response.Headers.CacheControl!.NoStore); var page = (await response.Content.ReadFromJsonAsync<RecordedPracticePage>())!;
        var audio = path + $"/{page.Questions[0].Id}/audio/{source.UserPieces[0]}?revision=1&locked=false";
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync(audio)).StatusCode); TestOwnerManagement.Authorize(client);
        response = await client.GetAsync(audio); Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("audio/wav", response.Content.Headers.ContentType!.MediaType);
        Assert.True(response.Headers.CacheControl!.NoStore); Assert.Equal("inline", response.Content.Headers.ContentDisposition!.DispositionType);
        using (CoreInvocationContext.Enter(InvocationOrigin.Agent)) await Assert.ThrowsAsync<DomainException>(() => host.Services.GetRequiredService<RecordedInterviewPractice>().GetAsync(source.Recording, false));
    }
}
