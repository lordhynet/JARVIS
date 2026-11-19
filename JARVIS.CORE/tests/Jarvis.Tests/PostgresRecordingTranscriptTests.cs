using System.Security.Cryptography;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Recordings;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task Recording_transcription_resumes_after_restart_without_replaying_paid_pieces_and_preserves_track_overlap()
    {
        await using var database = await TestDatabase.CreateAsync(); var clock = new TravelClock(); var provider = new RecordingTranscriberFake();
        var parent = Path.Combine(AppContext.BaseDirectory, "recording-transcript-pg-tests"); var folder = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)); var id = Guid.NewGuid(); AdvanceRecordingTranscriptRequest firstRequest;
        using var manual = CoreInvocationContext.Enter(InvocationOrigin.ManualClient);
        provider.Text.Enqueue("Why retries?"); provider.Text.Enqueue("For transient failures.");
        try {
            using (var root = new PostgresApiFactory(database.ConnectionString, clock))
            using (var first = root.WithWebHostBuilder(b => b.ConfigureServices(s => { s.AddSingleton<IRecordingTranscriber>(provider); s.AddSingleton<IRecordingAudioFiles>(new EncryptedRecordingFiles(() => new(true, folder, key))); }))) {
                var services = first.Services; await services.GetRequiredService<DeviceService>().RegisterAsync(new("transcript-laptop", "Laptop", DeviceKind.WindowsAgent, new(true, true, false, false, [])));
                var credential = await services.GetRequiredService<DeviceCredentials>().IssueAsync("transcript-laptop", default);
                var recordings = services.GetRequiredService<RecordingService>(); await recordings.PrepareAsync(new(id, "transcript-laptop", "Private recorded interview", true, true, Locked: false));
                using (DeviceCredentialContext.Enter(new("device:transcript-laptop", "transcript-laptop", PrivacyData.Hash(credential.Secret)))) {
                    await recordings.CaptureAsync(new("start", "transcript-laptop", id, false, true, true)); var wave = RecordingRig.Wav();
                    foreach (var track in new[] { "user", "interviewer" }) await recordings.CaptureAsync(new("chunk", "transcript-laptop", id, Track: track, Sequence: 0, Sha256: RecordingAudio.Hash(wave), AudioBase64: Convert.ToBase64String(wave)));
                    await recordings.CaptureAsync(new("finish", "transcript-laptop", id, FinalCounts: new Dictionary<string, int> { ["user"] = 1, ["interviewer"] = 1 }));
                }
                var service = services.GetRequiredService<RecordingTranscriptService>(); var recording = Assert.Single(await recordings.ListAsync());
                var quote = await service.QuoteAsync(id, new(recording.Revision, false)); firstRequest = RecordingTranscriptRig.Advance(quote);
                var partial = await service.AdvanceAsync(id, firstRequest); Assert.True(partial.Transcript!.Partial); Assert.Equal(1, provider.Calls);
            }
            using var secondRoot = new PostgresApiFactory(database.ConnectionString, clock);
            using var second = secondRoot.WithWebHostBuilder(b => b.ConfigureServices(s => { s.AddSingleton<IRecordingTranscriber>(provider); s.AddSingleton<IRecordingAudioFiles>(new EncryptedRecordingFiles(() => new(true, folder, key))); }));
            var restarted = second.Services.GetRequiredService<RecordingTranscriptService>(); var progress = (await restarted.GetAsync(id, false))!;
            await restarted.AdvanceAsync(id, firstRequest); Assert.Equal(1, provider.Calls);
            var complete = await restarted.AdvanceAsync(id, RecordingTranscriptRig.Advance(progress)); Assert.Equal(2, provider.Calls); Assert.False(complete.Transcript!.Partial);
            Assert.Equal(new[] { "interviewer", "user" }, complete.Transcript.Segments.Select(s => s.Speaker)); Assert.All(complete.Transcript.Segments, s => Assert.Equal(0, s.StartSeconds));
            var contexts = second.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>(); await using var db = await contexts.CreateDbContextAsync();
            Assert.DoesNotContain("Why retries?", (await db.InterviewRecordings.SingleAsync()).Json);
            var labelled = await restarted.RelabelAsync(id, new(complete.Transcript.Revision, complete.Transcript.Segments[0].RecordingPieceId!.Value, "unknown", false)); Assert.Equal("unknown", labelled.Segments[0].Speaker);
            await second.Services.GetRequiredService<ImportService>().DeleteAsync(labelled.Id, labelled.Revision);
            await Assert.ThrowsAsync<DomainException>(() => restarted.GetAsync(id, false));
        } finally { if (Path.GetDirectoryName(Path.GetFullPath(folder)) == Path.GetFullPath(parent) && Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
}
