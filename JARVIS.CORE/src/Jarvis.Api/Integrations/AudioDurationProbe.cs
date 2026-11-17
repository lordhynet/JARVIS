using System.Diagnostics;
using System.Globalization;
using Jarvis.Orchestrator.Runtime;

namespace Jarvis.Api.Integrations;

public sealed record PreparedAudio(string Path, double Seconds);
public interface IAudioDurationProbe
{
    Task<double> SecondsAsync(string path, CancellationToken ct);
    Task<PreparedAudio> PrepareAsync(string path, CancellationToken ct) => throw new DomainException(503, "A trusted audio decoder is required before paid transcription.");
}
public sealed class AudioDurationProbe(IConfiguration config) : IAudioDurationProbe
{
    public async Task<PreparedAudio> PrepareAsync(string path, CancellationToken ct)
    {
        var format = Path.GetExtension(path).ToLowerInvariant() switch {
            ".webm" => "matroska", ".mp4" or ".m4a" => "mov", ".wav" => "wav",
            _ => throw new DomainException(415, "Supported audio containers are WebM, MP4/M4A and WAV.")
        };
        var info = new ProcessStartInfo(config["Audio:Ffmpeg"] ?? "ffmpeg") {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        foreach (var arg in new[] { "-nostdin", "-v", "error", "-xerror", "-protocol_whitelist", "file,pipe", "-f", format,
            "-i", path, "-map", "0:a:0", "-vn", "-ac", "1", "-ar", "16000", "-f", "s16le", "pipe:1" }) info.ArgumentList.Add(arg);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(15));
        Process? process;
        try { process = Process.Start(info); }
        catch (System.ComponentModel.Win32Exception) { throw new DomainException(503, "Audio normalization requires ffmpeg."); }
        using (process)
        {
            Guard.Require(process is not null, 503, "Audio decoder could not start.");
            // Drain errors without retaining attacker-controlled output in memory or logs.
            var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, timeout.Token);
            using var pcm = new MemoryStream(); var buffer = new byte[8192];
            try {
                int count;
                while ((count = await process.StandardOutput.BaseStream.ReadAsync(buffer, timeout.Token)) > 0) {
                    Guard.Require(pcm.Length + count <= 180 * 32000, 400, "Decoded audio exceeds three minutes.");
                    pcm.Write(buffer, 0, count);
                }
                await process.WaitForExitAsync(timeout.Token); await errors;
                Guard.Require(process.ExitCode == 0 && pcm.Length > 0 && pcm.Length % 2 == 0, 400, "Audio could not be decoded safely.");
                var normalized = Path.Combine(Path.GetTempPath(), "jarvis-normalized-" + Guid.NewGuid().ToString("N") + ".wav");
                try {
                    await using var output = File.Create(normalized);
                    WriteWave(output, pcm.ToArray());
                    return new(normalized, pcm.Length / 32000d);
                } catch { if (File.Exists(normalized)) File.Delete(normalized); throw; }
            }
            finally {
                if (!process.HasExited) process.Kill(true);
                try { await errors; } catch (OperationCanceledException) { }
            }
        }
    }
    internal static void WriteWave(Stream output, byte[] pcm, int sampleRate = 16000)
    {
        using var writer = new BinaryWriter(output, System.Text.Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8); writer.Write(36 + pcm.Length); writer.Write("WAVEfmt "u8); writer.Write(16);
        writer.Write((short)1); writer.Write((short)1); writer.Write(sampleRate); writer.Write(sampleRate * 2);
        writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(pcm.Length); writer.Write(pcm);
    }
    public async Task<double> SecondsAsync(string path, CancellationToken ct)
    {
        var info = new ProcessStartInfo(config["Audio:Ffprobe"] ?? "ffprobe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", path }) info.ArgumentList.Add(arg);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(8));
        Process? process;
        try { process = Process.Start(info); }
        catch (System.ComponentModel.Win32Exception) { throw new DomainException(503, "Audio inspection requires ffprobe. The supplied Docker image includes it."); }
        using (process)
        {
            Guard.Require(process is not null, 503, "Audio inspection could not start.");
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token); var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { if (!process.HasExited) process.Kill(true); throw; }
            var text = await stdout; await stderr;
            Guard.Require(process.ExitCode == 0 && double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) &&
                double.IsFinite(seconds) && seconds is > 0 and <= 180, 400, "Audio must be decodable and no longer than three minutes.");
            return double.Parse(text.Trim(), CultureInfo.InvariantCulture);
        }
    }
}
