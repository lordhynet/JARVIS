using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Jarvis.Contracts;
using Jarvis.Orchestrator.Recordings;
using Jarvis.Orchestrator.Runtime;
using Jarvis.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Jarvis.Tests;

internal sealed class RecordingFilesFake : IRecordingAudioFiles
{
    public bool Ready { get; set; } = true; public bool FailErase; public long ExtraBytes; public int Writes; public Dictionary<Guid, byte[]> Blobs = [];
    public Action? BeforeScan; public Func<CancellationToken, Task>? BeforeWrite;
    public long BytesOnDisk { get { BeforeScan?.Invoke(); return ExtraBytes + Blobs.Values.Sum(b => (long)b.Length + 32); } }
    public async Task WriteAsync(RecordingChunk c, ReadOnlyMemory<byte> data, CancellationToken ct) { if (BeforeWrite is not null) await BeforeWrite(ct); Blobs[c.BlobId] = data.ToArray(); Writes++; }
    public Task<byte[]> ReadAsync(RecordingChunk c, CancellationToken ct) => Task.FromResult(Blobs[c.BlobId].ToArray());
    public Task DeleteAsync(RecordingChunk c, CancellationToken ct) { Blobs.Remove(c.BlobId); return Task.CompletedTask; }
    public Task DeleteRecordingAsync(Guid id, CancellationToken ct) { Blobs.Clear(); return Task.CompletedTask; }
    public Task EraseAsync(CancellationToken ct) { if (FailErase) throw new IOException("Fixture deletion failed."); Blobs.Clear(); return Task.CompletedTask; }
}
internal sealed class RecordingRig
{
    public TravelClock Clock = new(); public InMemoryRuntimeStore Runtime = new(); public MemoryPrivacyBoundary Boundary = new();
    public RecordingFilesFake Files = new(); public MemoryRecordingStore Store; public RecordingService Service;
    public const string Device = "recording-phone", Hash = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    public RecordingRig() { Store = new(Runtime, Boundary); Service = new(Store, Files, Clock); }
    public async Task<InterviewRecording> Prepare(bool windows = false)
    {
        await Runtime.UpdateAsync(s => { s.Devices[Device] = new(new(Device, "Phone", windows ? DeviceKind.WindowsAgent : DeviceKind.AndroidCompanion, new(true, true, false, false, []), Clock.Now), Clock.Now, true); s.DeviceCredentials[Device] = new(Device, Hash, Clock.Now); return true; });
        using var origin = CoreInvocationContext.Enter(InvocationOrigin.ManualClient);
        return await Service.PrepareAsync(new(Guid.NewGuid(), Device, "Private interview", true, true, Locked: false));
    }
    public IDisposable Actor() => DeviceCredentialContext.Enter(new("device:" + Device, Device, Hash));
    public Task<RecordingCaptureResult> Start(Guid id, bool? locked = false, bool consent = true) => Service.CaptureAsync(new("start", Device, id, locked, consent, consent));
    public Task<RecordingCaptureResult> Chunk(Guid id, int sequence, byte[] audio, string track = "unknown") => Service.CaptureAsync(new("chunk", Device, id, Track: track, Sequence: sequence, Sha256: RecordingAudio.Hash(audio), AudioBase64: Convert.ToBase64String(audio)));
    public Task<RecordingCaptureResult> Finish(Guid id, int count) => Service.CaptureAsync(new("finish", Device, id, FinalCounts: new Dictionary<string, int> { ["unknown"] = count }));
    public static byte[] Wav(int samples = 16000)
    {
        var bytes = new byte[44 + samples * 2]; "RIFF"u8.CopyTo(bytes); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), bytes.Length - 8); "WAVEfmt "u8.CopyTo(bytes.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 16); BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(20), 1); BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(24), 16000); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(28), 32000); BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(32), 2); BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(34), 16);
        "data"u8.CopyTo(bytes.AsSpan(36)); BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(40), samples * 2); bytes.AsSpan(44).Fill(41); return bytes;
    }
}
public sealed class RecordingTests
{
    [Fact]
    public async Task A_duplicate_is_not_acknowledged_when_its_stored_audio_is_missing()
    {
        var rig = new RecordingRig(); var recording = await rig.Prepare(); using var actor = rig.Actor(); await rig.Start(recording.Id);
        var wav = RecordingRig.Wav(); await rig.Chunk(recording.Id, 0, wav); rig.Files.Blobs.Clear();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => rig.Chunk(recording.Id, 0, wav)); Assert.Equal(1, rig.Files.Writes);
    }
    [Fact]
    public async Task Two_track_completeness_requires_both_tracks_and_recording_excludes_calibration()
    {
        var rig = new RecordingRig(); var recording = await rig.Prepare(windows: true); using var actor = rig.Actor();
        var sessions = new Jarvis.Orchestrator.Sessions.InMemorySessionStore(); var devices = new DeviceService(rig.Runtime, rig.Clock);
        var ownership = new AudioOwnership(rig.Runtime, sessions, devices, rig.Clock, new MemoryCoreTransaction(rig.Runtime, sessions, new()));
        var voice = await ownership.StartCalibrationAsync(RecordingRig.Device, default);
        await Assert.ThrowsAsync<DomainException>(() => rig.Start(recording.Id)); await ownership.ReleaseAsync(voice, default);
        await rig.Start(recording.Id); await Assert.ThrowsAsync<DomainException>(() => ownership.StartCalibrationAsync(RecordingRig.Device, default));
        await rig.Chunk(recording.Id, 0, RecordingRig.Wav(), "user");
        var finish = new RecordingCaptureRequest("finish", RecordingRig.Device, recording.Id, FinalCounts: new Dictionary<string, int> { ["user"] = 1, ["interviewer"] = 1 });
        Assert.Equal("Incomplete", (await rig.Service.CaptureAsync(finish)).Recording!.Status);
        await rig.Chunk(recording.Id, 0, RecordingRig.Wav(), "interviewer"); Assert.Equal("Complete", (await rig.Service.CaptureAsync(finish)).Recording!.Status);
        await ownership.StartCalibrationAsync(RecordingRig.Device, default);
    }
    [Fact]
    public async Task Erasure_does_not_report_success_or_remove_metadata_when_recording_files_cannot_be_deleted()
    {
        var fake = new RecordingFilesFake { FailErase = true }; using var original = new JarvisApiFactory();
        using var host = original.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IRecordingAudioFiles>(fake)));
        var privacy = host.Services.GetRequiredService<IPrivacyStore>(); var status = await privacy.StatusAsync();
        await Assert.ThrowsAsync<IOException>(() => privacy.EraseAsync(new(Guid.NewGuid(), status.Revision, PrivacyData.ErasePhrase), PrivacyData.Hash("owner"), DateTimeOffset.UtcNow));
        Assert.Equal(status, await privacy.StatusAsync());
    }
    [Theory]
    [InlineData(true, true)] [InlineData(null, true)] [InlineData(false, false)]
    public async Task Capture_requires_an_unlocked_confirmation_for_each_prepared_recording(bool? locked, bool consent)
    {
        var rig = new RecordingRig(); var recording = await rig.Prepare(); using var actor = rig.Actor();
        await Assert.ThrowsAsync<DomainException>(() => rig.Start(recording.Id, locked, consent));
        Assert.Null((await rig.Runtime.ReadAsync(s => s.Devices[RecordingRig.Device])).Recording); Assert.Equal(0, rig.Files.Writes);
    }
    [Fact]
    public async Task Chunks_resume_idempotently_and_a_gap_never_becomes_complete_without_the_missing_audio()
    {
        var rig = new RecordingRig(); var recording = await rig.Prepare(); using var actor = rig.Actor(); var started = await rig.Start(recording.Id);
        Assert.Equal("", started.Recording!.Title); Assert.Equal(started.Recording.StartedAt, (await rig.Start(recording.Id)).Recording!.StartedAt);
        var minute = RecordingRig.Wav(16000 * 60); await rig.Chunk(recording.Id, 0, minute); await rig.Chunk(recording.Id, 0, minute); Assert.Equal(1, rig.Files.Writes);
        await rig.Chunk(recording.Id, 2, minute); Assert.Equal("Incomplete", (await rig.Finish(recording.Id, 3)).Recording!.Status);
        await rig.Chunk(recording.Id, 1, minute); Assert.Equal("Complete", (await rig.Finish(recording.Id, 3)).Recording!.Status);
        Assert.Equal(minute.Length * 3L, (await rig.Service.ListAsync())[0].AudioBytes); Assert.Null((await rig.Runtime.ReadAsync(s => s.Devices[RecordingRig.Device])).Recording);
        var other = RecordingRig.Wav(); await Assert.ThrowsAsync<DomainException>(() => rig.Chunk(recording.Id, 0, other));
    }
    [Fact]
    public async Task Bounds_hash_track_quota_and_credential_binding_prevent_unacknowledged_writes()
    {
        var rig = new RecordingRig(); var recording = await rig.Prepare(); using var actor = rig.Actor(); await rig.Start(recording.Id); var audio = RecordingRig.Wav();
        await Assert.ThrowsAsync<DomainException>(() => rig.Chunk(recording.Id, 180, audio));
        await Assert.ThrowsAsync<DomainException>(() => rig.Chunk(recording.Id, 0, audio, "interviewer"));
        await Assert.ThrowsAsync<DomainException>(() => rig.Service.CaptureAsync(new("chunk", RecordingRig.Device, recording.Id, Track: "unknown", Sequence: 0, Sha256: new('0', 64), AudioBase64: Convert.ToBase64String(audio))));
        await Assert.ThrowsAsync<DomainException>(() => rig.Chunk(recording.Id, 0, new byte[46]));
        rig.Files.ExtraBytes = RecordingService.MaximumBytes - 1; await Assert.ThrowsAsync<DomainException>(() => rig.Chunk(recording.Id, 0, audio)); rig.Files.ExtraBytes = 0;
        using (DeviceCredentialContext.Enter(new("device:other", "other", RecordingRig.Hash))) await Assert.ThrowsAsync<DomainException>(() => rig.Chunk(recording.Id, 0, audio));
        Assert.Equal(0, rig.Files.Writes);
    }
    [Fact]
    public async Task Recording_blocks_voice_and_retention_removes_audio_without_deleting_metadata()
    {
        var rig = new RecordingRig(); var recording = await rig.Prepare(); using var actor = rig.Actor(); await rig.Start(recording.Id); await rig.Chunk(recording.Id, 0, RecordingRig.Wav());
        var devices = new DeviceService(rig.Runtime, rig.Clock);
        await Assert.ThrowsAsync<DomainException>(() => devices.RequireOnlineAsync(RecordingRig.Device, true, default));
        Assert.Contains("Private interview", JsonSerializer.Serialize(await rig.Service.ListAsync()));
        rig.Clock.Now = rig.Clock.Now.AddDays(31); await rig.Service.RetainAudioAsync(); Assert.Empty(rig.Files.Blobs);
        var retained = Assert.Single(await rig.Service.ListAsync()); Assert.True(retained.AudioDeleted); Assert.Equal("Private interview", retained.Title); Assert.Equal(0, retained.AudioBytes);
        Assert.Null((await rig.Runtime.ReadAsync(s => s.Devices[RecordingRig.Device])).Recording);
        Assert.DoesNotContain("Private interview", await rig.Runtime.ReadAsync(s => JsonSerializer.Serialize(s)));
    }
    [Fact]
    public async Task Real_ciphertext_roundtrip_authenticates_metadata_and_never_contains_the_PCM_pattern()
    {
        var parent = Path.Combine(AppContext.BaseDirectory, "recording-tests"); var root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)); var files = new EncryptedRecordingFiles(() => new(true, root, key)); var audio = RecordingRig.Wav();
        var chunk = new RecordingChunk(Guid.NewGuid(), Guid.NewGuid(), "unknown", 0, RecordingAudio.Hash(audio), 16000, Guid.NewGuid(), 2, audio.Length, DateTimeOffset.UtcNow);
        try {
            await files.WriteAsync(chunk, audio, default); var disk = Assert.Single(Directory.GetFiles(root, "*.jra", SearchOption.AllDirectories)); var cipher = await File.ReadAllBytesAsync(disk);
            Assert.DoesNotContain(Convert.ToHexString(audio.AsSpan(44, 64)), Convert.ToHexString(cipher)); Assert.Equal(audio, await files.ReadAsync(chunk, default));
            await Assert.ThrowsAsync<AuthenticationTagMismatchException>(() => files.ReadAsync(chunk with { Track = "user" }, default));
            var other = chunk with { Id = Guid.NewGuid(), RecordingId = Guid.NewGuid(), BlobId = Guid.NewGuid() }; await files.WriteAsync(other, audio, default);
            await files.DeleteRecordingAsync(chunk.RecordingId, default); Assert.Equal(audio, await files.ReadAsync(other, default));
            await Assert.ThrowsAsync<DomainException>(() => files.ReadAsync(chunk, default));
            await files.EraseAsync(default); Assert.Equal(0, files.BytesOnDisk);
        } finally { if (Path.GetDirectoryName(Path.GetFullPath(root)) == Path.GetFullPath(parent) && Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Fact]
    public async Task Owner_management_is_separate_from_device_capture_and_export_and_erasure_cover_recordings()
    {
        var fake = new RecordingFilesFake(); using var original = new JarvisApiFactory(); using var host = original.WithWebHostBuilder(b => b.ConfigureServices(s => s.AddSingleton<IRecordingAudioFiles>(fake)));
        using var owner = host.CreateClient(); owner.DefaultRequestHeaders.Add("X-Jarvis-Key", JarvisApiFactory.TestKey); TestOwnerManagement.Authorize(owner); var services = host.Services;
        await services.GetRequiredService<DeviceService>().RegisterAsync(new("interview-phone", "Phone", DeviceKind.AndroidCompanion, new(true, true, false, false, [])));
        var credential = await services.GetRequiredService<DeviceCredentials>().IssueAsync("interview-phone", default); using var native = BrowserEnrollmentTests.Browser(host, credential);
        var request = new PrepareRecordingRequest(Guid.NewGuid(), "interview-phone", "Interview metadata", true, true, Locked: false);
        using var missingConsent = await owner.PostAsJsonAsync("/api/recordings", request with { ConfirmRecordingAuthorised = false }); Assert.Equal(HttpStatusCode.BadRequest, missingConsent.StatusCode);
        using var prepared = await owner.PostAsJsonAsync("/api/recordings", request); prepared.EnsureSuccessStatusCode();
        using var locked = await owner.GetAsync("/api/recordings?locked=true"); Assert.Equal(HttpStatusCode.Forbidden, locked.StatusCode);
        using var denied = await native.GetAsync("/api/recordings?locked=false"); Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using var started = await native.PostAsJsonAsync(RecordingProtocol.Capture, new RecordingCaptureRequest("start", "interview-phone", request.RequestId, false, true, true)); started.EnsureSuccessStatusCode();
        var audio = RecordingRig.Wav(); using var uploaded = await native.PostAsJsonAsync(RecordingProtocol.Capture, new RecordingCaptureRequest("chunk", "interview-phone", request.RequestId, Track: "unknown", Sequence: 0, Sha256: RecordingAudio.Hash(audio), AudioBase64: Convert.ToBase64String(audio))); uploaded.EnsureSuccessStatusCode();
        var export = (await owner.GetFromJsonAsync<CoreDataExport>("/api/privacy/export", LifeScenarioTests.Json))!; Assert.Single(export.InterviewRecordings!);
        var serialized = JsonSerializer.Serialize(export); Assert.DoesNotContain(Convert.ToBase64String(audio), serialized); Assert.DoesNotContain(PrivacyData.Hash(credential.Secret), serialized);
        var privacy = (await owner.GetFromJsonAsync<PrivacyStatus>("/api/privacy/status"))!; using var erased = await owner.PostAsJsonAsync("/api/privacy/erase", new EraseCoreRequest(Guid.NewGuid(), privacy.Revision, PrivacyData.ErasePhrase)); erased.EnsureSuccessStatusCode();
        Assert.Empty(fake.Blobs); Assert.Empty(services.GetRequiredService<MemoryPrivacyBoundary>().Recordings.Sessions);
    }
}
