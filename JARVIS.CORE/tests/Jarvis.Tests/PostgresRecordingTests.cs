using System.Security.Cryptography;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Recordings;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

public sealed partial class PostgresSessionTests
{
    [PostgresFact]
    public async Task Encrypted_recording_and_acks_survive_restart_and_retention_and_erasure_remove_real_files()
    {
        await using var database = await TestDatabase.CreateAsync(); var clock = new TravelClock();
        var parent = Path.Combine(AppContext.BaseDirectory, "recording-pg-tests"); var folder = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)); var id = Guid.NewGuid(); string credentialHash;
        using var invocation = CoreInvocationContext.Enter(InvocationOrigin.ManualClient);
        try {
            using (var root = new PostgresApiFactory(database.ConnectionString, clock))
            using (var first = root.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IRecordingAudioFiles>(new EncryptedRecordingFiles(() => new(true, folder, key)))))) {
                var services = first.Services;
                await services.GetRequiredService<DeviceService>().RegisterAsync(new("record-phone", "Phone", DeviceKind.AndroidCompanion, new(true, true, false, false, [])));
                var credential = await services.GetRequiredService<DeviceCredentials>().IssueAsync("record-phone", default); credentialHash = PrivacyData.Hash(credential.Secret);
                var recordings = services.GetRequiredService<RecordingService>(); await recordings.PrepareAsync(new(id, "record-phone", "PRIVATE PG INTERVIEW", true, true, Locked: false));
                using var actor = DeviceCredentialContext.Enter(new("device:record-phone", "record-phone", credentialHash));
                await recordings.CaptureAsync(new("start", "record-phone", id, false, true, true));
                var wav = RecordingRig.Wav(); await recordings.CaptureAsync(new("chunk", "record-phone", id, Track: "unknown", Sequence: 0, Sha256: RecordingAudio.Hash(wav), AudioBase64: Convert.ToBase64String(wav)));
                Assert.Single(Directory.GetFiles(folder, "*.jra", SearchOption.AllDirectories));
                Assert.DoesNotContain("PRIVATE PG INTERVIEW", await services.GetRequiredService<IRuntimeStore>().ReadAsync(s => System.Text.Json.JsonSerializer.Serialize(s)));
            }
            using var secondRoot = new PostgresApiFactory(database.ConnectionString, clock);
            using var second = secondRoot.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IRecordingAudioFiles>(new EncryptedRecordingFiles(() => new(true, folder, key)))));
            var service = second.Services.GetRequiredService<RecordingService>();
            using (DeviceCredentialContext.Enter(new("device:record-phone", "record-phone", credentialHash))) {
                var wav = RecordingRig.Wav(); await service.CaptureAsync(new("chunk", "record-phone", id, Track: "unknown", Sequence: 0, Sha256: RecordingAudio.Hash(wav), AudioBase64: Convert.ToBase64String(wav)));
                Assert.Single(Directory.GetFiles(folder, "*.jra", SearchOption.AllDirectories));
                var request = new RecordingCaptureRequest("finish", "record-phone", id, FinalCounts: new Dictionary<string, int> { ["unknown"] = 1 });
                var result = await service.CaptureAsync(request); Assert.Equal("Complete", result.Recording!.Status); Assert.Equal(result.Recording.Revision, (await service.CaptureAsync(request)).Recording!.Revision);
            }
            var store = second.Services.GetRequiredService<IRecordingStore>(); var chunk = await store.ReadAsync(s => Assert.Single(s.Chunks.Values));
            Assert.Equal(RecordingRig.Wav(), await second.Services.GetRequiredService<IRecordingAudioFiles>().ReadAsync(chunk, default));
            clock.Now = clock.Now.AddDays(31); await service.RetainAudioAsync(); Assert.Empty(Directory.GetFiles(folder, "*.jra", SearchOption.AllDirectories)); Assert.True(Assert.Single(await service.ListAsync()).AudioDeleted);
            var next = Guid.NewGuid(); await service.PrepareAsync(new(next, "record-phone", "Second private recording", true, true, Locked: false));
            using (DeviceCredentialContext.Enter(new("device:record-phone", "record-phone", credentialHash))) {
                await service.CaptureAsync(new("start", "record-phone", next, false, true, true)); var wav = RecordingRig.Wav();
                await service.CaptureAsync(new("chunk", "record-phone", next, Track: "unknown", Sequence: 0, Sha256: RecordingAudio.Hash(wav), AudioBase64: Convert.ToBase64String(wav)));
            }
            var privacy = second.Services.GetRequiredService<IPrivacyStore>(); var status = await privacy.StatusAsync();
            await privacy.EraseAsync(new(Guid.NewGuid(), status.Revision, PrivacyData.ErasePhrase), PrivacyData.Hash("fixture-core-key"), clock.Now);
            Assert.Empty(Directory.GetFiles(folder, "*.jra", SearchOption.AllDirectories));
            var factory = second.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>(); await using var db = await factory.CreateDbContextAsync(); Assert.Empty(await db.InterviewRecordings.ToArrayAsync()); Assert.Empty(await db.RecordingChunks.ToArrayAsync());
        } finally { if (Path.GetDirectoryName(Path.GetFullPath(folder)) == Path.GetFullPath(parent) && Directory.Exists(folder)) Directory.Delete(folder, true); }
    }
    [PostgresFact]
    public async Task Recording_migration_downgrade_removes_capture_exclusion_and_reapplies_without_changing_cold_state()
    {
        await using var database = await TestDatabase.CreateAsync(); using var host = new PostgresApiFactory(database.ConnectionString, new TravelClock());
        var runtime = host.Services.GetRequiredService<IRuntimeStore>();
        await runtime.UpdateAsync(s => { s.Devices["fixture"] = new(new("fixture", "Fixture", DeviceKind.AndroidCompanion, new(true, true, false, false, []), DateTimeOffset.UtcNow), DateTimeOffset.UtcNow, true, new(Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(3))); return true; });
        var contexts = host.Services.GetRequiredService<IDbContextFactory<SessionDbContext>>(); await using var db = await contexts.CreateDbContextAsync();
        var cold = (await db.RuntimeDocuments.AsNoTracking().SingleAsync()).Json;
        await db.Database.GetService<IMigrator>().MigrateAsync("20261118194620_TravelRoutesAndMonitor");
        Assert.DoesNotContain("Recording", (await db.RuntimeAudioState.AsNoTracking().SingleAsync()).DevicesJson);
        await db.Database.MigrateAsync(); Assert.Empty(await db.InterviewRecordings.ToArrayAsync()); Assert.Equal(cold, (await db.RuntimeDocuments.AsNoTracking().SingleAsync()).Json);
    }
}
