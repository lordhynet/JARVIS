using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Jarvis.Contracts;

namespace Jarvis.Windows.Client;

public sealed record RecordingQueueState(Guid RecordingId, string DeviceId, long Epoch, string Binding,
    IReadOnlyList<string> Tracks, Dictionary<string, int> Counts, DateTimeOffset StartedAt, bool Stopped = false, bool Discontinuous = false);
public sealed record QueuedRecordingChunk(Guid RecordingId, string Track, int Sequence, string Sha256, string AudioBase64);
public sealed class RecordingUploadQueue
{
    private readonly string folder; private readonly ICredentialProtector protector; private readonly object gate = new();
    public RecordingQueueState State { get; private set; }
    private RecordingUploadQueue(string folder, ICredentialProtector protector, RecordingQueueState state) { this.folder = folder; this.protector = protector; State = state; CheckDirectory(); }
    public static string Binding(Connection connection, string deviceId, long epoch) => Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes($"recording-v1:{connection.Origin}:{deviceId}:{epoch}")));
    public static RecordingUploadQueue Create(string root, ICredentialProtector protector, RecordingQueueState state)
    {
        Validate(state); var path = Path.Combine(Path.GetFullPath(root), state.RecordingId.ToString("N"));
        var queue = new RecordingUploadQueue(path, protector, state); if (File.Exists(queue.StatePath)) throw new InvalidOperationException("This recording already has a protected queue.");
        Directory.CreateDirectory(path); queue.Persist(); return queue;
    }
    public static IReadOnlyList<RecordingUploadQueue> Load(string root, ICredentialProtector protector, Func<RecordingQueueState, bool>? predicate = null)
    {
        CheckAncestors(Path.GetFullPath(root));
        if (!Directory.Exists(root)) return [];
        var result = new List<RecordingUploadQueue>();
        foreach (var folder in Directory.EnumerateDirectories(root)) {
            if (!Guid.TryParseExact(Path.GetFileName(folder), "N", out var id)) continue;
            if ((File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0) throw new IOException("Protected recording queue cannot use symbolic links.");
            var stateFile = Path.Combine(folder, "state.dpapi"); if (!File.Exists(stateFile)) continue;
            var state = Read<RecordingQueueState>(stateFile, protector); Validate(state);
            if (state.RecordingId != id) throw new InvalidOperationException("Protected recording identity does not match its folder.");
            if (predicate is not null && !predicate(state)) continue;
            var queue = new RecordingUploadQueue(folder, protector, state);
            foreach (var track in state.Tracks) for (var sequence = 0; sequence < 180; sequence++)
                if (File.Exists(queue.ChunkPath(track, sequence))) queue.State.Counts[track] = Math.Max(queue.State.Counts[track], sequence + 1);
            // A restarted process cannot silently resume microphone capture or claim the lost tail was complete.
            if (!state.Stopped) queue.State = queue.State with { Stopped = true, Discontinuous = true };
            queue.Persist(); result.Add(queue);
        }
        return result;
    }
    public static void EraseOrigin(string root, ICredentialProtector protector, Connection connection, RecordingUploadQueue? active = null)
    {
        active?.Clear();
        foreach (var pending in Load(root, protector, state => state.Binding == Binding(connection, state.DeviceId, state.Epoch))) pending.Clear();
    }
    private static void Validate(RecordingQueueState state)
    {
        if (state.RecordingId == Guid.Empty || state.DeviceId.Length is < 1 or > 80 || state.Epoch < 0 || state.Binding.Length != 64 ||
            state.Tracks.Count is < 1 or > 2 || state.Tracks.Distinct().Count() != state.Tracks.Count || state.Tracks.Any(t => t is not ("user" or "interviewer" or "unknown")) ||
            state.Counts.Count != state.Tracks.Count || state.Tracks.Any(t => !state.Counts.TryGetValue(t, out var n) || n is < 0 or > 180)) throw new InvalidOperationException("Protected recording state is invalid.");
    }
    private string StatePath => Path.Combine(folder, "state.dpapi");
    private void CheckDirectory() => CheckAncestors(folder);
    private static void CheckAncestors(string path)
    {
        for (var info = new DirectoryInfo(path); info is not null; info = info.Parent)
            if (info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Protected recording queue cannot traverse symbolic links.");
    }
    private string ChunkPath(string track, int sequence)
    {
        if (!State.Tracks.Contains(track) || sequence is < 0 or >= 180) throw new InvalidOperationException("Recording chunk identity is invalid.");
        CheckDirectory(); return Path.Combine(folder, $"{track}-{sequence:D3}.dpapi");
    }
    private static T Read<T>(string path, ICredentialProtector protector)
    {
        if (new FileInfo(path).Length > 3_000_000 || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Protected recording file is invalid.");
        var plain = protector.Unprotect(File.ReadAllBytes(path));
        try { return JsonSerializer.Deserialize<T>(plain) ?? throw new InvalidOperationException("Protected recording data is invalid."); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private void Write<T>(string path, T value)
    {
        CheckDirectory(); var plain = JsonSerializer.SerializeToUtf8Bytes(value);
        try {
            var cipher = protector.Protect(plain);
            var bytes = Directory.EnumerateFiles(Path.GetDirectoryName(folder)!, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }).Sum(f => new FileInfo(f).Length);
            if (bytes + cipher.Length > 2L * 1024 * 1024 * 1024) throw new IOException("Protected recording upload queue is full.");
            var temporary = path + ".tmp";
            foreach (var candidate in new[] { path, temporary }) if (File.Exists(candidate) && (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0) throw new IOException("Protected recording file cannot be a symbolic link.");
            try { File.WriteAllBytes(temporary, cipher); File.Move(temporary, path, true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        } finally { CryptographicOperations.ZeroMemory(plain); }
    }
    private void Persist() => Write(StatePath, State);
    public void Add(string track, byte[] wav)
    {
        lock (gate) {
            if (State.Stopped || wav.Length is <= 44 or > 1_920_044) throw new InvalidOperationException("Recording capture already stopped or exceeded the chunk bound.");
            var sequence = State.Counts[track]; var path = ChunkPath(track, sequence);
            var data = new QueuedRecordingChunk(State.RecordingId, track, sequence, Convert.ToHexString(SHA256.HashData(wav)), Convert.ToBase64String(wav));
            Write(path, data); State.Counts[track]++; Persist();
        }
    }
    public IReadOnlyList<QueuedRecordingChunk> Pending(int limit = 4)
    {
        lock (gate) {
            CheckDirectory(); var result = new List<QueuedRecordingChunk>();
            foreach (var track in State.Tracks) for (var sequence = 0; sequence < 180; sequence++) {
                var path = ChunkPath(track, sequence); if (!File.Exists(path)) continue; var chunk = Read<QueuedRecordingChunk>(path, protector);
                if (chunk.RecordingId != State.RecordingId || chunk.Track != track || chunk.Sequence != sequence || chunk.Sha256.Length != 64) throw new InvalidOperationException("Protected chunk identity changed.");
                result.Add(chunk);
                if (result.Count >= limit) return result;
            }
            return result;
        }
    }
    public void Acknowledge(RecordingChunkAck ack)
    {
        lock (gate) {
            var path = ChunkPath(ack.Track, ack.Sequence); if (!File.Exists(path)) return;
            var chunk = Read<QueuedRecordingChunk>(path, protector); if (chunk.Sha256 != ack.Sha256) throw new InvalidOperationException("Core acknowledgement did not match the protected chunk."); File.Delete(path);
        }
    }
    public void Stop(bool discontinuous) { lock (gate) { State = State with { Stopped = true, Discontinuous = State.Discontinuous || discontinuous }; Persist(); } }
    public void Clear()
    {
        lock (gate) {
            CheckDirectory(); if (!Directory.Exists(folder)) return;
            foreach (var track in State.Tracks) for (var i = 0; i < 180; i++) { var path = ChunkPath(track, i); File.Delete(path); File.Delete(path + ".tmp"); }
            File.Delete(StatePath); File.Delete(StatePath + ".tmp"); if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder);
        }
    }
}

public static class RecordingWave
{
    public static byte[] Wrap(ReadOnlySpan<byte> pcm)
    {
        if (pcm.Length == 0 || pcm.Length > 1_920_000 || pcm.Length % 2 != 0) throw new InvalidOperationException("PCM recording frame is invalid.");
        var wav = new byte[pcm.Length + 44]; "RIFF"u8.CopyTo(wav); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), wav.Length - 8); "WAVEfmt "u8.CopyTo(wav.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(16), 16); BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(20), 1); BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(24), 16000); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(28), 32000); BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(32), 2); BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 16);
        "data"u8.CopyTo(wav.AsSpan(36)); BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), pcm.Length); pcm.CopyTo(wav.AsSpan(44)); return wav;
    }
}
