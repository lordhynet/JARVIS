using System.Security.Cryptography;
using System.Text;
using Jarvis.Contracts;
using Jarvis.Windows.Client;

namespace Jarvis.Windows.Tests;

public sealed class RecordingTests
{
    private sealed class Protector : ICredentialProtector
    {
        private readonly byte[] key = RandomNumberGenerator.GetBytes(32);
        public byte[] Protect(byte[] value) { var nonce = RandomNumberGenerator.GetBytes(12); var result = new byte[value.Length + 28]; nonce.CopyTo(result, 0); using var aes = new AesGcm(key, 16); aes.Encrypt(nonce, value, result.AsSpan(28), result.AsSpan(12, 16)); return result; }
        public byte[] Unprotect(byte[] value) { var plain = new byte[value.Length - 28]; using var aes = new AesGcm(key, 16); aes.Decrypt(value.AsSpan(0, 12), value.AsSpan(28), value.AsSpan(12, 16), plain); return plain; }
    }
    private sealed class Transport(Func<RecordingCaptureRequest, RecordingCaptureResult> send) : IRecordingCaptureTransport
    {
        public Task<RecordingCaptureResult> SendAsync(RecordingCaptureRequest request, CancellationToken ct) => Task.FromResult(send(request));
    }
    private static RecordingQueueState State() => new(Guid.NewGuid(), "laptop", 2, new string('A', 64), ["user", "interviewer"], new() { ["user"] = 0, ["interviewer"] = 0 }, DateTimeOffset.UtcNow);
    private static InterviewRecording View(Guid id, string status = "Recording") => new(id, "laptop", "", status, ["user", "interviewer"], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(10));
    [Fact]
    public void Confirmed_erasure_removes_active_and_older_origin_queues_without_touching_another_Core()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "recording-tests", Guid.NewGuid().ToString("N")); var protector = new Protector();
        var first = new Connection(new Uri("https://first.example"), "jdv1_" + new string('A', 64));
        var other = new Connection(new Uri("https://other.example"), first.Credential);
        try {
            var a = State(); a = a with { Binding = RecordingUploadQueue.Binding(first, a.DeviceId, a.Epoch) };
            var active = RecordingUploadQueue.Create(root, protector, a); active.Add("user", RecordingWave.Wrap(new byte[32000]));
            var old = State(); old = old with { Epoch = 1, Binding = RecordingUploadQueue.Binding(first, old.DeviceId, 1) };
            var older = RecordingUploadQueue.Create(root, protector, old); older.Add("user", RecordingWave.Wrap(new byte[32000])); older.Stop(false);
            var b = State(); b = b with { Binding = RecordingUploadQueue.Binding(other, b.DeviceId, b.Epoch) };
            var untouched = RecordingUploadQueue.Create(root, protector, b); untouched.Add("user", RecordingWave.Wrap(new byte[32000])); untouched.Stop(false);
            var stateFile = Path.Combine(root, b.RecordingId.ToString("N"), "state.dpapi"); var before = File.ReadAllBytes(stateFile);
            RecordingUploadQueue.EraseOrigin(root, protector, first, active);
            Assert.Equal(before, File.ReadAllBytes(stateFile));
            var remaining = Assert.Single(RecordingUploadQueue.Load(root, protector)); Assert.Equal(b.Binding, remaining.State.Binding); Assert.Single(remaining.Pending());
            RecordingUploadQueue.EraseOrigin(root, protector, first); Assert.Single(remaining.Pending());
        } finally { var full = Path.GetFullPath(root); if (full.StartsWith(Path.Combine(AppContext.BaseDirectory, "recording-tests") + Path.DirectorySeparatorChar) && Directory.Exists(full)) Directory.Delete(full, true); }
    }
    [Fact]
    public async Task Two_track_protected_chunks_survive_a_drop_and_are_removed_only_after_matching_acknowledgement()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "recording-tests", Guid.NewGuid().ToString("N")); var protector = new Protector(); var state = State(); var queue = RecordingUploadQueue.Create(root, protector, state);
        try {
            var wav = RecordingWave.Wrap(Enumerable.Repeat((byte)41, 32000).ToArray()); queue.Add("user", wav); queue.Add("interviewer", wav); queue.Stop(false);
            Assert.All(Directory.GetFiles(root, "*.dpapi", SearchOption.AllDirectories), file => Assert.DoesNotContain(Convert.ToBase64String(wav), Encoding.UTF8.GetString(File.ReadAllBytes(file))));
            var attempts = 0;
            var pump = new RecordingUploadPump(queue, new Transport(request => {
                if (++attempts == 1) throw new HttpRequestException("Synthetic drop");
                return new(true, [], View(state.RecordingId, request.Action == "finish" ? "Complete" : "Recording"), request.Action == "chunk" ? [new(request.Track!, request.Sequence!.Value, request.Sha256!)] : []);
            }));
            await Assert.ThrowsAsync<HttpRequestException>(() => pump.FlushAsync(default)); Assert.Equal(2, queue.Pending().Count);
            Assert.True(await pump.FlushAsync(default)); Assert.Empty(Directory.GetFiles(root, "*.dpapi", SearchOption.AllDirectories));
        } finally { var full = Path.GetFullPath(root); if (full.StartsWith(Path.Combine(AppContext.BaseDirectory, "recording-tests") + Path.DirectorySeparatorChar) && Directory.Exists(full)) Directory.Delete(full, true); }
    }
    [Fact]
    public async Task A_wrong_ack_retains_audio_and_restart_does_not_reopen_the_microphone_or_hide_a_lost_tail()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "recording-tests", Guid.NewGuid().ToString("N")); var protector = new Protector(); var state = State();
        try {
            var queue = RecordingUploadQueue.Create(root, protector, state); queue.Add("user", RecordingWave.Wrap(new byte[32000]));
            var wrong = new RecordingUploadPump(queue, new Transport(r => new(true, [], View(state.RecordingId), [new("user", 0, new string('0', 64))])));
            await Assert.ThrowsAsync<InvalidOperationException>(() => wrong.FlushAsync(default)); Assert.Single(queue.Pending());
            var recovered = Assert.Single(RecordingUploadQueue.Load(root, protector)); Assert.True(recovered.State.Stopped); Assert.True(recovered.State.Discontinuous); Assert.Equal(1, recovered.State.Counts["user"]); Assert.Equal(0, recovered.State.Counts["interviewer"]);
            recovered.Clear(); Assert.Empty(Directory.GetFiles(root, "*.dpapi", SearchOption.AllDirectories));
        } finally { var full = Path.GetFullPath(root); if (full.StartsWith(Path.Combine(AppContext.BaseDirectory, "recording-tests") + Path.DirectorySeparatorChar) && Directory.Exists(full)) Directory.Delete(full, true); }
    }
    [Fact]
    public void Chunking_preserves_track_samples_with_a_bounded_final_tail()
    {
        var output = new List<byte[]>(); var chunks = new RecordingSampleBuffer(output.Add);
        chunks.Add(new byte[1_920_000]); chunks.Add(new byte[32000]); chunks.Flush(); Assert.Equal(2, output.Count);
        Assert.Equal(1_920_044, output[0].Length); Assert.Equal(32044, output[1].Length); Assert.Equal("RIFF", Encoding.ASCII.GetString(output[1], 0, 4));
    }
}
