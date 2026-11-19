using System.Security.Cryptography;
using Jarvis.Orchestrator.Recordings;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Persistence;

public sealed record RecordingFileOptions(bool Enabled, string Directory, string? EncryptionKey);
public sealed class EncryptedRecordingFiles(Func<RecordingFileOptions> options) : IRecordingAudioFiles
{
    private readonly string root = ValidateRoot(options().Directory);
    public bool Ready { get { var value = options(); var key = Decode(value.EncryptionKey); var ready = value.Enabled && key is not null; if (key is not null) CryptographicOperations.ZeroMemory(key); return ready; } }
    private static byte[]? Decode(string? value)
    {
        if (value is not { Length: 44 }) return null;
        try { var key = Convert.FromBase64String(value); if (key.Length == 32) return key; CryptographicOperations.ZeroMemory(key); return null; } catch (FormatException) { return null; }
    }
    private byte[] Key() { var value = options(); Guard.Require(value.Enabled, 503, "Recording encryption is not configured."); var key = Decode(value.EncryptionKey); Guard.Require(key is not null, 503, "Recording encryption is not configured."); return key; }
    private static string ValidateRoot(string directory)
    {
        Guard.Require(!string.IsNullOrWhiteSpace(directory), 503, "Recording storage directory is missing.");
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, directory));
        Guard.Require(!string.Equals(path.TrimEnd(Path.DirectorySeparatorChar), Path.GetPathRoot(path)?.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase), 503, "Recording storage cannot be a filesystem root.");
        return path;
    }
    private void Check(string path)
    {
        var full = Path.GetFullPath(path); var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        Guard.Require(full == root || full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase), 500, "Recording path escaped its storage directory.");
        if (Directory.Exists(full)) Guard.Require((File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0, 503, "Recording directories cannot be symbolic links.");
        for (var info = new DirectoryInfo(Path.GetDirectoryName(full) ?? root); info is not null; info = info.Parent)
            Guard.Require(!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) == 0, 503, "Recording storage cannot traverse symbolic links or reparse points.");
        if (File.Exists(full)) Guard.Require((File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0, 503, "Recording files cannot be symbolic links.");
    }
    private string PathFor(RecordingChunk chunk)
    {
        Guard.Require(chunk.Epoch >= 0 && chunk.RecordingId != Guid.Empty && chunk.BlobId != Guid.Empty, 500, "Recording blob identity is invalid.");
        var path = Path.Combine(root, "epoch-" + chunk.Epoch, chunk.RecordingId.ToString("N"), chunk.BlobId.ToString("N") + ".jra"); Check(path); return path;
    }
    private IEnumerable<string> OwnedFiles(Guid? recording = null)
    {
        Check(root); if (!Directory.Exists(root)) yield break;
        foreach (var epoch in Directory.EnumerateDirectories(root, "epoch-*")) {
            Check(Path.Combine(epoch, "probe")); if (!long.TryParse(Path.GetFileName(epoch)[6..], out var number) || number < 0) continue;
            foreach (var directory in Directory.EnumerateDirectories(epoch)) {
                Check(Path.Combine(directory, "probe")); if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out var id) || recording is not null && recording != id) continue;
                foreach (var file in Directory.EnumerateFiles(directory)) {
                    Check(file); var name = Path.GetFileName(file); var extension = Path.GetExtension(file);
                    if (extension is ".jra" or ".tmp" && Guid.TryParseExact(name[..^4], "N", out _)) yield return file;
                }
            }
        }
    }
    public long BytesOnDisk => OwnedFiles().Sum(path => new FileInfo(path).Length);
    public async Task WriteAsync(RecordingChunk chunk, ReadOnlyMemory<byte> audio, CancellationToken ct)
    {
        var path = PathFor(chunk); var key = Key(); var encrypted = new byte[audio.Length + 32];
        "JRA1"u8.CopyTo(encrypted); RandomNumberGenerator.Fill(encrypted.AsSpan(4, 12));
        try {
            using (var aes = new AesGcm(key, 16)) aes.Encrypt(encrypted.AsSpan(4, 12), audio.Span, encrypted.AsSpan(32), encrypted.AsSpan(16, 16), RecordingAudio.AssociatedData(chunk));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); Check(path);
            var temporary = Path.ChangeExtension(path, ".tmp"); Check(temporary);
            try {
                await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough)) {
                    await output.WriteAsync(encrypted, ct); await output.FlushAsync(ct); output.Flush(true);
                }
                File.Move(temporary, path, false);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        } finally { CryptographicOperations.ZeroMemory(key); }
    }
    public async Task<byte[]> ReadAsync(RecordingChunk chunk, CancellationToken ct)
    {
        var path = PathFor(chunk); Guard.Require(File.Exists(path), 404, "Recording audio is missing or was removed.");
        Guard.Require(new FileInfo(path).Length == chunk.Bytes + 32 && chunk.Bytes <= RecordingAudio.MaximumChunkBytes, 409, "Recording audio length does not match its metadata.");
        var key = Key(); byte[]? plain = null;
        try {
            var encrypted = await File.ReadAllBytesAsync(path, ct); plain = new byte[encrypted.Length - 32];
            Guard.Require(encrypted.AsSpan(0, 4).SequenceEqual("JRA1"u8), 409, "Recording audio header is invalid.");
            using var aes = new AesGcm(key, 16); aes.Decrypt(encrypted.AsSpan(4, 12), encrypted.AsSpan(32), encrypted.AsSpan(16, 16), plain, RecordingAudio.AssociatedData(chunk));
            Guard.Require(RecordingAudio.Hash(plain) == chunk.Sha256, 409, "Recording audio integrity check failed."); return plain;
        } catch { if (plain is not null) CryptographicOperations.ZeroMemory(plain); throw; } finally { CryptographicOperations.ZeroMemory(key); }
    }
    public Task DeleteAsync(RecordingChunk chunk, CancellationToken ct) { ct.ThrowIfCancellationRequested(); var path = PathFor(chunk); File.Delete(path); return Task.CompletedTask; }
    public Task DeleteRecordingAsync(Guid id, CancellationToken ct) { foreach (var file in OwnedFiles(id).ToArray()) { ct.ThrowIfCancellationRequested(); File.Delete(file); } return Task.CompletedTask; }
    public Task EraseAsync(CancellationToken ct) { foreach (var file in OwnedFiles().ToArray()) { ct.ThrowIfCancellationRequested(); File.Delete(file); } return Task.CompletedTask; }
}
