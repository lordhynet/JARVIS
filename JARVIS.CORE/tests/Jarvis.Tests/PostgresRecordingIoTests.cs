using System.Data.Common;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Recordings;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    private sealed class LostRecordingCommit : DbTransactionInterceptor
    {
        private int armed = 1;
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken ct = default) {
            if (Interlocked.Exchange(ref armed, 0) == 1) throw new IOException("Synthetic lost commit response"); return Task.CompletedTask;
        }
    }
    private static async Task<(Guid Id, string Hash)> PrepareIoRecording(IServiceProvider services)
    {
        using var manual = CoreInvocationContext.Enter(InvocationOrigin.ManualClient); const string device = "recording-io";
        await services.GetRequiredService<DeviceService>().RegisterAsync(new(device, "IO fixture", DeviceKind.AndroidCompanion, new(true, true, false, false, [])));
        var credential = await services.GetRequiredService<DeviceCredentials>().IssueAsync(device, default); var id = Guid.NewGuid(); var hash = PrivacyData.Hash(credential.Secret);
        await services.GetRequiredService<RecordingService>().PrepareAsync(new(id, device, "Private IO fixture", true, true, Locked: false));
        using var actor = DeviceCredentialContext.Enter(new("device:" + device, device, hash));
        await services.GetRequiredService<RecordingService>().CaptureAsync(new("start", device, id, false, true, true)); return (id, hash);
    }
    [PostgresFact]
    public async Task A_stalled_recording_write_allows_hot_runtime_updates_and_read_only_poll_but_erasure_waits_for_it()
    {
        await using var database = await TestDatabase.CreateAsync(); var files = new RecordingFilesFake(); using var root = new PostgresApiFactory(database.ConnectionString);
        using var host = root.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IRecordingAudioFiles>(files))); var services = host.Services;
        var (id, hash) = await PrepareIoRecording(services); var recordings = services.GetRequiredService<RecordingService>();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        files.BeforeWrite = async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); };
        async Task<RecordingCaptureResult> Capture(RecordingCaptureRequest request) { using var actor = DeviceCredentialContext.Enter(new("device:recording-io", "recording-io", hash)); return await recordings.CaptureAsync(request); }
        var wave = RecordingRig.Wav(); var upload = Capture(new("chunk", "recording-io", id, Track: "unknown", Sequence: 0, Sha256: RecordingAudio.Hash(wave), AudioBase64: Convert.ToBase64String(wave)));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); Task<PrivacyStatus>? erasing = null;
        try {
            await services.GetRequiredService<IRuntimeStore>().UpdateAsync(_ => true).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True((await Capture(new("poll", "recording-io")).WaitAsync(TimeSpan.FromSeconds(10))).Ready);
            var privacy = services.GetRequiredService<IPrivacyStore>(); var status = await privacy.StatusAsync();
            erasing = privacy.EraseAsync(new(Guid.NewGuid(), status.Revision, PrivacyData.ErasePhrase), PrivacyData.Hash("fixture-owner"), DateTimeOffset.UtcNow);
            await Task.Delay(100); Assert.False(erasing.IsCompleted); Assert.False(upload.IsCompleted);
        } finally { release.TrySetResult(); }
        Assert.Single((await upload).Acknowledged!); Assert.True((await erasing!).Erased); Assert.Empty(files.Blobs);
        var contexts = services.GetRequiredService<IDbContextFactory<SessionDbContext>>(); await using var db = await contexts.CreateDbContextAsync(); Assert.Empty(await db.RecordingChunks.ToArrayAsync());
    }
    [PostgresFact]
    public async Task Revoking_a_capture_credential_during_staging_rejects_the_commit_and_removes_the_blob()
    {
        await using var database = await TestDatabase.CreateAsync(); var files = new RecordingFilesFake(); using var root = new PostgresApiFactory(database.ConnectionString);
        using var host = root.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IRecordingAudioFiles>(files))); var services = host.Services;
        var (id, hash) = await PrepareIoRecording(services);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        files.BeforeWrite = async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); };
        using var actor = DeviceCredentialContext.Enter(new("device:recording-io", "recording-io", hash)); var wave = RecordingRig.Wav();
        var upload = services.GetRequiredService<RecordingService>().CaptureAsync(new("chunk", "recording-io", id, Track: "unknown", Sequence: 0, Sha256: RecordingAudio.Hash(wave), AudioBase64: Convert.ToBase64String(wave)));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try {
            await services.GetRequiredService<IRuntimeStore>().UpdateAsync(s => {
                s.DeviceCredentials["recording-io"] = s.DeviceCredentials["recording-io"] with { Revoked = true }; return true;
            }).WaitAsync(TimeSpan.FromSeconds(10));
        } finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<DomainException>(() => upload); Assert.Empty(files.Blobs);
        Assert.Empty(await services.GetRequiredService<IRecordingStore>().ReadAsync(s => s.Chunks.Values.ToArray()));
    }
    [PostgresFact]
    public async Task An_uncertain_recording_commit_retains_its_blob_and_a_retry_acknowledges_it_without_another_write()
    {
        await using var database = await TestDatabase.CreateAsync(); var files = new RecordingFilesFake(); using var root = new PostgresApiFactory(database.ConnectionString);
        using var host = root.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IRecordingAudioFiles>(files))); var services = host.Services;
        var (id, hash) = await PrepareIoRecording(services);
        var options = new DbContextOptionsBuilder<SessionDbContext>().UseNpgsql(database.ConnectionString).AddInterceptors(new LostRecordingCommit()).Options;
        var faulty = new RecordingService(new PostgresRecordingStore(new SplitReadFactory(options), services.GetRequiredService<AudioFenceChanges>()), files, services.GetRequiredService<TimeProvider>());
        var wave = RecordingRig.Wav(); var request = new RecordingCaptureRequest("chunk", "recording-io", id, Track: "unknown", Sequence: 0, Sha256: RecordingAudio.Hash(wave), AudioBase64: Convert.ToBase64String(wave));
        using var actor = DeviceCredentialContext.Enter(new("device:recording-io", "recording-io", hash));
        await Assert.ThrowsAsync<IOException>(() => faulty.CaptureAsync(request)); Assert.Single(files.Blobs);
        var result = await services.GetRequiredService<RecordingService>().CaptureAsync(request); Assert.Single(result.Acknowledged!); Assert.Equal(1, files.Writes);
    }
}
